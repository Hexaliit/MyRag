using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// In-memory per-session LFU cache that promotes frequently-relevant segments
/// and evicts stale ones. Used by InteractiveAskLoop to carry context across
/// conversation turns.
/// </summary>
public sealed class SalientSegmentCache
{
    private readonly Dictionary<string, CachedSegment> _segments = new();
    private readonly int _capacity;
    private int _totalEvicted;

    /// <summary>
    /// Prompt-level salience index: maps prompt embeddings to their resolved
    /// salient segment sets. Avoids re-computing N cosine similarities when
    /// a similar prompt has already been resolved.
    /// </summary>
    public PromptSalienceIndex PromptIndex { get; }

    public SalientSegmentCache(int capacity = 50, int promptIndexCapacity = 30, float promptHitThreshold = 0.85f)
    {
        _capacity = capacity;
        PromptIndex = new PromptSalienceIndex(promptIndexCapacity, promptHitThreshold);
    }

    public sealed class CachedSegment
    {
        public required ContentItem Item { get; init; }
        public int AccessCount { get; set; }
        public int TurnAdded { get; init; }
        public int LastAccessedTurn { get; set; }
        public float LastSalience { get; set; }
    }

    /// <summary>
    /// Add new segments from retrieval results. Existing segments get their
    /// turn updated but are not replaced.
    /// </summary>
    public void AddRange(IEnumerable<ContentItem> items, int currentTurn)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Id)) continue;

            if (_segments.TryGetValue(item.Id, out var existing))
            {
                existing.LastAccessedTurn = currentTurn;
            }
            else
            {
                _segments[item.Id] = new CachedSegment
                {
                    Item = item,
                    AccessCount = 0,
                    TurnAdded = currentTurn,
                    LastAccessedTurn = currentTurn,
                    LastSalience = 0f
                };
            }
        }
    }

    /// <summary>
    /// Get segments salient to the current query. Uses the PromptSalienceIndex
    /// as a fast path: if a similar prompt was already resolved, its cached
    /// segment set is returned directly (saving N cosine computations).
    /// Falls back to full cosine scan on cache miss.
    /// </summary>
    public List<ContentItem> GetSalient(
        float[] queryEmbedding, int currentTurn,
        float threshold = 0.35f, int maxItems = 10)
    {
        if (queryEmbedding is not { Length: > 0 })
            return [];

        // Fast path: check prompt index for a near-duplicate prompt
        var cachedIds = PromptIndex.TryGetSalient(queryEmbedding);
        if (cachedIds != null)
        {
            var items = new List<ContentItem>();
            foreach (var id in cachedIds)
            {
                if (_segments.TryGetValue(id, out var seg))
                {
                    seg.AccessCount++;
                    seg.LastAccessedTurn = currentTurn;
                    items.Add(seg.Item);
                }
            }
            // Use cached result if at least half the original IDs are still valid
            if (items.Count >= Math.Max(1, cachedIds.Count / 2))
                return items.Take(maxItems).ToList();
        }

        // Slow path: full cosine scan against all cached segments
        var scored = new List<(CachedSegment seg, float sim)>();

        foreach (var entry in _segments.Values)
        {
            if (entry.Item.Embedding is not { Length: > 0 }) continue;

            var sim = VectorMath.CosineSimilarity(queryEmbedding, entry.Item.Embedding);
            if (sim >= threshold)
                scored.Add((entry, sim));
        }

        var results = scored
            .OrderByDescending(x => x.sim)
            .Take(maxItems)
            .ToList();

        // Update access tracking for returned segments
        foreach (var (seg, sim) in results)
        {
            seg.AccessCount++;
            seg.LastAccessedTurn = currentTurn;
            seg.LastSalience = sim;
        }

        var resultItems = results.Select(x => x.seg.Item).ToList();

        // Record this prompt → salient mapping for future fast-path lookups
        var resultIds = resultItems.Select(i => i.Id).ToList();
        PromptIndex.Record(queryEmbedding, resultIds, currentTurn);

        return resultItems;
    }

    /// <summary>
    /// Evict stale and low-frequency segments.
    /// Phase 1: Remove segments not accessed in the last <paramref name="staleTurns"/> turns.
    /// Phase 2: If still over capacity, remove lowest AccessCount segments.
    /// </summary>
    public void Evict(int currentTurn, int staleTurns = 5)
    {
        // Phase 1: staleness eviction
        var staleIds = _segments
            .Where(kv => currentTurn - kv.Value.LastAccessedTurn > staleTurns)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            _segments.Remove(id);
            _totalEvicted++;
        }

        // Phase 2: LFU eviction when over capacity
        if (_segments.Count > _capacity)
        {
            var toRemove = _segments
                .OrderBy(kv => kv.Value.AccessCount)
                .ThenBy(kv => kv.Value.LastAccessedTurn)
                .Take(_segments.Count - _capacity)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in toRemove)
            {
                _segments.Remove(id);
                _totalEvicted++;
            }
        }

        // Prune prompt index entries that reference evicted segments
        PromptIndex.Prune(GetAllIds());
    }

    /// <summary>
    /// Get all cached segment IDs (for diagnostics).
    /// </summary>
    public IReadOnlyList<string> GetAllIds()
        => _segments.Keys.ToList();

    /// <summary>
    /// Cache statistics for diagnostics.
    /// </summary>
    public (int count, int capacity, int evicted) Stats
        => (_segments.Count, _capacity, _totalEvicted);
}

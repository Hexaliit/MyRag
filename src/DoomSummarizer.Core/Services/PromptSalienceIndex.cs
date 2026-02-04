namespace DoomSummarizer.Services;

/// <summary>
///     Cross-cutting prompt cache that maps prompt embeddings to their resolved
///     salient segment sets. When a new prompt is similar to a previous one
///     (cosine > threshold), the cached salient set is reused directly — saving
///     N cosine similarity computations against every cached segment.
///     Uses LRU eviction to bound memory. Each entry stores the prompt embedding,
///     the resolved segment IDs, and the turn it was recorded at.
/// </summary>
public sealed class PromptSalienceIndex
{
    private readonly int _capacity;
    private readonly LinkedList<PromptEntry> _entries = new();
    private readonly float _hitThreshold;
    private int _hits;
    private int _misses;

    public PromptSalienceIndex(int capacity = 30, float hitThreshold = 0.85f)
    {
        _capacity = capacity;
        _hitThreshold = hitThreshold;
    }

    /// <summary>
    ///     Cache hit/miss statistics.
    /// </summary>
    public (int entries, int hits, int misses, double hitRate) Stats
    {
        get
        {
            var total = _hits + _misses;
            var rate = total > 0 ? (double)_hits / total : 0;
            return (_entries.Count, _hits, _misses, rate);
        }
    }

    /// <summary>
    ///     Try to find a cached salient set for a query embedding.
    ///     Returns the segment IDs if a close-enough previous prompt exists,
    ///     or null on cache miss.
    ///     On hit, the matching entry is promoted to the front (LRU).
    /// </summary>
    public List<string>? TryGetSalient(float[] queryEmbedding)
    {
        var bestSim = 0f;
        LinkedListNode<PromptEntry>? bestNode = null;

        var node = _entries.First;
        while (node != null)
        {
            var sim = VectorMath.CosineSimilarity(queryEmbedding, node.Value.PromptEmbedding);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestNode = node;
            }

            node = node.Next;
        }

        if (bestNode != null && bestSim >= _hitThreshold)
        {
            // LRU promotion: move to front
            _entries.Remove(bestNode);
            _entries.AddFirst(bestNode);
            _hits++;
            return new List<string>(bestNode.Value.SalientSegmentIds);
        }

        _misses++;
        return null;
    }

    /// <summary>
    ///     Record a prompt → salient segment mapping for future lookups.
    ///     Called after SalientSegmentCache.GetSalient() computes the full result.
    /// </summary>
    public void Record(float[] promptEmbedding, List<string> salientSegmentIds, int turn)
    {
        // Don't record empty results
        if (salientSegmentIds.Count == 0) return;

        // Check if we already have a near-duplicate prompt
        var node = _entries.First;
        while (node != null)
        {
            var sim = VectorMath.CosineSimilarity(promptEmbedding, node.Value.PromptEmbedding);
            if (sim >= _hitThreshold)
            {
                // Update existing entry with fresh segment IDs and promote to front
                _entries.Remove(node);
                _entries.AddFirst(new LinkedListNode<PromptEntry>(new PromptEntry
                {
                    PromptEmbedding = promptEmbedding,
                    SalientSegmentIds = salientSegmentIds,
                    Turn = turn
                }));
                return;
            }

            node = node.Next;
        }

        // New entry
        _entries.AddFirst(new PromptEntry
        {
            PromptEmbedding = promptEmbedding,
            SalientSegmentIds = salientSegmentIds,
            Turn = turn
        });

        // LRU eviction
        while (_entries.Count > _capacity)
            _entries.RemoveLast();
    }

    /// <summary>
    ///     Invalidate entries that reference segments no longer in the cache.
    ///     Call after SalientSegmentCache.Evict() to keep the index consistent.
    /// </summary>
    public void Prune(IReadOnlyList<string> validSegmentIds)
    {
        var validSet = new HashSet<string>(validSegmentIds);
        var node = _entries.First;
        while (node != null)
        {
            var next = node.Next;
            // Remove entries where all salient segments have been evicted
            node.Value.SalientSegmentIds.RemoveAll(id => !validSet.Contains(id));
            if (node.Value.SalientSegmentIds.Count == 0)
                _entries.Remove(node);
            node = next;
        }
    }

    /// <summary>
    ///     Clear all entries (e.g., when conversation is cleared).
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        _hits = 0;
        _misses = 0;
    }

    /// <summary>
    ///     A cached mapping from a prompt embedding to its resolved salient segments.
    /// </summary>
    private sealed class PromptEntry
    {
        public required float[] PromptEmbedding { get; init; }
        public required List<string> SalientSegmentIds { get; init; }
        public required int Turn { get; init; }
    }
}
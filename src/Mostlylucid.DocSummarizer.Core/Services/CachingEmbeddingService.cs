using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     LFU (Least Frequently Used) caching decorator for any <see cref="IEmbeddingService" />.
///     Caches embedding results keyed by XxHash64 of the input text. Thread-safe.
///     Avoids recomputing embeddings for identical text (queries, anchors, entity names).
/// </summary>
/// <remarks>
///     The cache uses an O(1) approximate LFU eviction: when the cache reaches max capacity,
///     the entry with the lowest (frequency, lastAccessed) is evicted. Frequency is incremented
///     on each hit. Batch operations check the cache per-item, only computing uncached items
///     through the inner service's batch method to preserve GPU batching efficiency.
/// </remarks>
public sealed class CachingEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly IEmbeddingService _inner;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<ulong, CacheEntry> _cache = new();

    // Diagnostics
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _totalComputeMs;

    public CachingEmbeddingService(IEmbeddingService inner, int maxEntries = 8192)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxEntries = maxEntries > 0 ? maxEntries : 8192;
    }

    public int EmbeddingDimension => _inner.EmbeddingDimension;

    public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return await _inner.EmbedAsync(text, ct);

        var key = HashText(text);

        if (_cache.TryGetValue(key, out var entry))
        {
            entry.IncrementFrequency();
            Interlocked.Increment(ref _hits);
            return entry.Embedding;
        }

        var sw = Stopwatch.StartNew();
        var embedding = await _inner.EmbedAsync(text, ct);
        sw.Stop();
        Interlocked.Add(ref _totalComputeMs, sw.ElapsedMilliseconds);
        Interlocked.Increment(ref _misses);

        TryAdd(key, embedding);
        return embedding;
    }

    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return Array.Empty<float[]>();

        var results = new float[textList.Count][];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();

        // Check cache for each item
        for (var i = 0; i < textList.Count; i++)
        {
            var text = textList[i];
            if (string.IsNullOrEmpty(text))
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(text);
                continue;
            }

            var key = HashText(text);
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.IncrementFrequency();
                Interlocked.Increment(ref _hits);
                results[i] = entry.Embedding;
            }
            else
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(text);
            }
        }

        // Batch-embed only uncached items (preserves GPU batching efficiency)
        if (uncachedTexts.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var computed = await _inner.EmbedBatchAsync(uncachedTexts, ct);
            sw.Stop();
            Interlocked.Add(ref _totalComputeMs, sw.ElapsedMilliseconds);
            Interlocked.Add(ref _misses, uncachedTexts.Count);

            for (var i = 0; i < uncachedIndices.Count; i++)
            {
                results[uncachedIndices[i]] = computed[i];

                var text = uncachedTexts[i];
                if (!string.IsNullOrEmpty(text))
                    TryAdd(HashText(text), computed[i]);
            }
        }

        return results;
    }

    /// <summary>
    ///     Get current cache statistics for diagnostics.
    /// </summary>
    public CacheStats GetStats() => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        _cache.Count,
        _maxEntries,
        Interlocked.Read(ref _evictions),
        Interlocked.Read(ref _totalComputeMs));

    public void Dispose()
    {
        _cache.Clear();
        (_inner as IDisposable)?.Dispose();
    }

    private static ulong HashText(string text)
        => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

    private void TryAdd(ulong key, float[] embedding)
    {
        var entry = new CacheEntry(embedding);

        if (_cache.TryAdd(key, entry))
        {
            // Evict if over capacity
            if (_cache.Count > _maxEntries)
                EvictLfu();
        }
        else
        {
            // Key was added concurrently — just bump frequency on existing entry
            if (_cache.TryGetValue(key, out var existing))
                existing.IncrementFrequency();
        }
    }

    /// <summary>
    ///     Evict the least-frequently-used entry. O(N) scan — acceptable because eviction
    ///     only triggers when cache is full, and N is bounded by maxEntries (default 8K).
    /// </summary>
    private void EvictLfu()
    {
        ulong victimKey = 0;
        var victimScore = long.MaxValue;
        var found = false;

        foreach (var kvp in _cache)
        {
            // Score = frequency * 1000 + recency tiebreaker (lower is worse)
            var score = (long)kvp.Value.Frequency * 1000 + (kvp.Value.LastAccessedTicks % 1000);
            if (score < victimScore)
            {
                victimScore = score;
                victimKey = kvp.Key;
                found = true;
            }
        }

        if (found && _cache.TryRemove(victimKey, out _))
            Interlocked.Increment(ref _evictions);
    }

    private sealed class CacheEntry
    {
        public readonly float[] Embedding;
        private int _frequency;
        private long _lastAccessedTicks;

        public CacheEntry(float[] embedding)
        {
            Embedding = embedding;
            _frequency = 1;
            _lastAccessedTicks = Environment.TickCount64;
        }

        public int Frequency => _frequency;
        public long LastAccessedTicks => Volatile.Read(ref _lastAccessedTicks);

        public void IncrementFrequency()
        {
            Interlocked.Increment(ref _frequency);
            Volatile.Write(ref _lastAccessedTicks, Environment.TickCount64);
        }
    }

    public readonly record struct CacheStats(
        long Hits,
        long Misses,
        int CurrentSize,
        int MaxSize,
        long Evictions,
        long TotalComputeMs)
    {
        public double HitRate => Hits + Misses == 0 ? 0.0 : (double)Hits / (Hits + Misses);
    }
}

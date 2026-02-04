using System.Collections.Concurrent;
using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Caching;

/// <summary>
/// In-memory LRU cache with semantic key matching.
/// Cache keys are embeddings matched by cosine similarity >= 0.92.
/// TTL varies by temporal scope.
/// </summary>
public class InMemoryDecompositionCache : IDecompositionCache
{
    private readonly ConcurrentDictionary<string, CacheItem> _cache = new();
    private readonly ILogger<InMemoryDecompositionCache>? _logger;

    /// <summary>Cosine similarity threshold for cache key matching.</summary>
    private const float SimilarityThreshold = 0.92f;

    /// <summary>Maximum cache entries (LRU eviction beyond this).</summary>
    private const int MaxEntries = 500;

    public InMemoryDecompositionCache(ILogger<InMemoryDecompositionCache>? logger = null)
    {
        _logger = logger;
    }

    public Task<CacheHit?> GetAsync(
        float[] queryEmbedding,
        TemporalScope? temporalScope,
        CancellationToken ct = default)
    {
        CacheHit? bestHit = null;
        var bestSim = 0f;

        foreach (var (key, item) in _cache)
        {
            if (ct.IsCancellationRequested) break;

            // Check expiry
            if (DateTimeOffset.UtcNow > item.ExpiresAt)
                continue;

            // Check temporal scope compatibility
            if (!IsTemporallyCompatible(temporalScope, item.TemporalScope))
                continue;

            var sim = ComplexityClassifier.CosineSimilarity(queryEmbedding, item.Embedding);
            if (sim >= SimilarityThreshold && sim > bestSim)
            {
                bestSim = sim;
                bestHit = new CacheHit
                {
                    CacheKey = key,
                    Similarity = sim,
                    CachedAt = item.CachedAt,
                    CachedResult = item.Result
                };
            }
        }

        if (bestHit != null)
            _logger?.LogDebug("Cache hit: sim={Sim:F3}, key={Key}", bestSim, bestHit.CacheKey);

        return Task.FromResult(bestHit);
    }

    public Task PutAsync(
        float[] queryEmbedding,
        TemporalScope? temporalScope,
        SubQueryResult result,
        CancellationToken ct = default)
    {
        // LRU eviction
        if (_cache.Count >= MaxEntries)
        {
            var oldest = _cache.OrderBy(kv => kv.Value.LastAccessedAt).FirstOrDefault();
            if (oldest.Key != null)
                _cache.TryRemove(oldest.Key, out _);
        }

        var ttl = GetTtl(temporalScope);
        var key = Guid.NewGuid().ToString("N")[..12];

        _cache[key] = new CacheItem
        {
            Embedding = queryEmbedding,
            Result = result,
            TemporalScope = temporalScope,
            CachedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + ttl,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        return Task.CompletedTask;
    }

    public Task EvictExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _cache.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
            _cache.TryRemove(key, out _);

        if (expired.Count > 0)
            _logger?.LogDebug("Evicted {Count} expired cache entries", expired.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// TTL tiers based on temporal scope.
    /// </summary>
    private static TimeSpan GetTtl(TemporalScope? scope) =>
        scope switch
        {
            { RequiresFresh: true, TimeSensitivity: "breaking" } => TimeSpan.FromMinutes(30),
            { RequiresFresh: true } => TimeSpan.FromHours(1),
            { TimeSensitivity: "today" } => TimeSpan.FromHours(2),
            { TimeSensitivity: "week" } => TimeSpan.FromHours(6),
            _ => TimeSpan.FromHours(24)
        };

    /// <summary>
    /// Check if a cached entry's temporal scope is compatible with the requested scope.
    /// </summary>
    private static bool IsTemporallyCompatible(TemporalScope? requested, TemporalScope? cached)
    {
        // No temporal requirement  -  anything cached is fine
        if (requested == null) return true;

        // Fresh request can't use non-fresh cache
        if (requested.RequiresFresh && cached is not { RequiresFresh: true })
            return false;

        return true;
    }

    private record CacheItem
    {
        public required float[] Embedding { get; init; }
        public required SubQueryResult Result { get; init; }
        public TemporalScope? TemporalScope { get; init; }
        public DateTimeOffset CachedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset LastAccessedAt { get; set; }
    }
}

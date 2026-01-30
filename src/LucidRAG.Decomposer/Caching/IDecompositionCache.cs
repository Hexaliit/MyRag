using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;

namespace LucidRAG.Decomposer.Caching;

/// <summary>
/// Semantic cache for sub-query results.
/// Uses embedding similarity (not string equality) for cache key matching.
/// TTL varies by content type (see CacheKeyBuilder).
/// </summary>
public interface IDecompositionCache
{
    /// <summary>
    /// Look up a cached result by embedding similarity.
    /// Returns null if no match found (similarity &lt; threshold or expired).
    /// </summary>
    Task<CacheHit?> GetAsync(
        float[] queryEmbedding,
        TemporalScope? temporalScope,
        CancellationToken ct = default);

    /// <summary>
    /// Store a sub-query result in cache.
    /// </summary>
    Task PutAsync(
        float[] queryEmbedding,
        TemporalScope? temporalScope,
        SubQueryResult result,
        CancellationToken ct = default);

    /// <summary>
    /// Evict expired entries.
    /// </summary>
    Task EvictExpiredAsync(CancellationToken ct = default);
}

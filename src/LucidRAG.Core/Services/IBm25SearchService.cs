using LucidRAG.Entities;

namespace LucidRAG.Services;

/// <summary>
/// Provider-agnostic full-text search interface.
/// Implementations: PostgresBM25Service (PostgreSQL ts_rank_cd), LuceneBM25Service (Lucene.NET).
/// </summary>
public interface IBm25SearchService
{
    /// <summary>
    /// Search evidence artifacts using full-text search ranking.
    /// </summary>
    Task<List<(EvidenceArtifact artifact, double score)>> SearchAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Search with explicit score retrieval.
    /// </summary>
    Task<List<(EvidenceArtifact artifact, double score)>> SearchWithScoresAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default);
}

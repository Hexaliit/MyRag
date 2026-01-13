using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services.Deduplication;

/// <summary>
///     Service interface for segment deduplication.
/// </summary>
public interface IDeduplicationService
{
    /// <summary>
    ///     Deduplicate segments during ingestion (intra-document only).
    ///     Near-duplicates boost salience; exact duplicates are dropped.
    /// </summary>
    /// <param name="segments">Segments to deduplicate</param>
    /// <param name="documentId">Optional document ID for logging</param>
    /// <returns>Deduplication result with metrics</returns>
    DeduplicationResult<Segment> DeduplicateForIngestion(
        List<Segment> segments,
        string? documentId = null);

    /// <summary>
    ///     Deduplicate results during retrieval (cross-document, post-RRF).
    ///     Keeps the highest-scoring item when duplicates are found.
    /// </summary>
    /// <typeparam name="T">Type of items being deduplicated</typeparam>
    /// <param name="rankedResults">Ranked results (already sorted by score)</param>
    /// <param name="getEmbedding">Function to extract embedding from each item</param>
    /// <param name="queryId">Optional query ID for logging</param>
    /// <returns>Deduplication result with metrics</returns>
    DeduplicationResult<T> DeduplicateForRetrieval<T>(
        List<T> rankedResults,
        Func<T, float[]?> getEmbedding,
        string? queryId = null) where T : class;
}

using Mostlylucid.Storage.Core.Abstractions.Models;

namespace Mostlylucid.Storage.Core.Abstractions;

/// <summary>
/// Extended vector store with support for named vectors (multi-modal embeddings).
/// Allows storing and searching across multiple embedding spaces per document
/// (e.g., "voice", "visual", "color", "motion").
/// </summary>
public interface IMultiVectorStore : IVectorStore
{
    /// <summary>
    /// Initialize a collection with both a primary schema and additional named vectors.
    /// </summary>
    /// <param name="collectionName">Collection name</param>
    /// <param name="primarySchema">Schema for the primary embedding vector</param>
    /// <param name="namedVectors">Additional named vector configurations</param>
    /// <param name="ct">Cancellation token</param>
    Task InitializeMultiVectorAsync(
        string collectionName,
        VectorStoreSchema primarySchema,
        IEnumerable<NamedVectorConfig> namedVectors,
        CancellationToken ct = default);

    /// <summary>
    /// Upsert documents with named vectors.
    /// The primary embedding is stored via the base path; named vectors are stored alongside.
    /// </summary>
    /// <param name="collectionName">Collection name</param>
    /// <param name="documents">Documents with named vectors</param>
    /// <param name="ct">Cancellation token</param>
    Task UpsertMultiVectorDocumentsAsync(
        string collectionName,
        IEnumerable<MultiVectorDocument> documents,
        CancellationToken ct = default);

    /// <summary>
    /// Search against a specific named vector.
    /// </summary>
    /// <param name="collectionName">Collection name</param>
    /// <param name="vectorName">Named vector to search (e.g., "voice", "visual")</param>
    /// <param name="query">Search query with embedding for the named vector space</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Ranked search results</returns>
    Task<List<VectorSearchResult>> SearchByNamedVectorAsync(
        string collectionName,
        string vectorName,
        VectorSearchQuery query,
        CancellationToken ct = default);
}

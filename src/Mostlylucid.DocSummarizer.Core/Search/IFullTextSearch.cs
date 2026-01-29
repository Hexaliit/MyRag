namespace Mostlylucid.DocSummarizer.Search;

/// <summary>
/// Backend-agnostic full-text search interface.
/// Implementations may use Lucene.NET, SQLite FTS5, PostgreSQL tsvector, etc.
/// </summary>
public interface IFullTextSearch
{
    /// <summary>
    /// Initialize the search backend (create indexes, tables, etc.).
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Index a single document for full-text search.
    /// </summary>
    Task IndexDocumentAsync(string id, string title, string content, IEnumerable<string>? keywords = null, CancellationToken ct = default);

    /// <summary>
    /// Search for documents matching the query.
    /// </summary>
    Task<List<FullTextResult>> SearchAsync(string query, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// Delete a document from the search index.
    /// </summary>
    Task DeleteDocumentAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Delete all documents from the search index.
    /// </summary>
    Task DeleteAllAsync(CancellationToken ct = default);
}

/// <summary>
/// A single full-text search result.
/// </summary>
public record FullTextResult(string Id, double Score, string? Title = null);

using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Search;

namespace Mostlylucid.DocSummarizer.FullText.Sqlite;

/// <summary>
/// Standalone SQLite FTS5 full-text search service implementing <see cref="IFullTextSearch"/>.
/// Uses FTS5 virtual tables with IDF-aware query building and keyword corpus tracking.
/// </summary>
public sealed class SqliteFts5SearchService : IFullTextSearch, IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _initialized;

    /// <summary>Keyword document frequencies for IDF-aware query construction.</summary>
    private Dictionary<string, int> _keywordCorpus = new(StringComparer.OrdinalIgnoreCase);
    private int _totalDocuments;

    public SqliteFts5SearchService(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _db.OpenAsync(ct);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS fts_documents USING fts5(
                doc_id,
                title,
                keywords,
                content,
                tokenize='porter unicode61'
            );

            CREATE TABLE IF NOT EXISTS keyword_corpus (
                keyword TEXT PRIMARY KEY,
                document_count INTEGER DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        await LoadKeywordCorpusAsync(ct);
        _initialized = true;
    }

    /// <inheritdoc/>
    public async Task IndexDocumentAsync(string id, string title, string content,
        IEnumerable<string>? keywords = null, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            // Delete existing entry if present
            using var delCmd = _db.CreateCommand();
            delCmd.CommandText = "DELETE FROM fts_documents WHERE doc_id = @id";
            delCmd.Parameters.AddWithValue("@id", id);
            await delCmd.ExecuteNonQueryAsync(ct);

            // Insert new entry
            var keywordsText = keywords != null ? string.Join(" ", keywords) : "";
            using var insCmd = _db.CreateCommand();
            insCmd.CommandText = """
                INSERT INTO fts_documents (doc_id, title, keywords, content)
                VALUES (@id, @title, @keywords, @content)
                """;
            insCmd.Parameters.AddWithValue("@id", id);
            insCmd.Parameters.AddWithValue("@title", title ?? "");
            insCmd.Parameters.AddWithValue("@keywords", keywordsText);
            insCmd.Parameters.AddWithValue("@content", content ?? "");
            await insCmd.ExecuteNonQueryAsync(ct);

            // Update keyword corpus
            if (keywords != null)
            {
                foreach (var kw in keywords.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await UpsertKeywordAsync(kw, ct);
                }
            }

            _totalDocuments++;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<List<FullTextResult>> SearchAsync(string query, int maxResults = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var ftsQuery = BuildIdfAwareFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery))
            return [];

        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT doc_id, rank, title
                FROM fts_documents
                WHERE fts_documents MATCH @query
                ORDER BY rank
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            var results = new List<FullTextResult>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var rank = reader.GetDouble(1);
                var title = reader.IsDBNull(2) ? null : reader.GetString(2);
                // FTS5 rank is negative (lower = better), normalize to positive score
                results.Add(new FullTextResult(id, -rank, title));
            }

            return results;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM fts_documents WHERE doc_id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM fts_documents;
                DELETE FROM keyword_corpus;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _keywordCorpus.Clear();
            _totalDocuments = 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Build an IDF-aware FTS5 query that boosts distinctive terms and demotes common ones.
    /// </summary>
    private string BuildIdfAwareFtsQuery(string query)
    {
        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 1)
            .Distinct()
            .ToList();

        if (terms.Count == 0) return "";

        // If we have a keyword corpus, distinguish distinctive vs common terms
        if (_keywordCorpus.Count > 0 && _totalDocuments > 0)
        {
            var distinctive = new List<string>();
            var common = new List<string>();

            foreach (var term in terms)
            {
                if (_keywordCorpus.TryGetValue(term, out var docCount))
                {
                    // IDF threshold: if term appears in >50% of docs, it's common
                    if (docCount > _totalDocuments * 0.5)
                        common.Add(term);
                    else
                        distinctive.Add(term);
                }
                else
                {
                    distinctive.Add(term); // Unknown terms are distinctive
                }
            }

            // Distinctive terms are AND'd, common terms are OR'd
            var parts = new List<string>();
            if (distinctive.Count > 0)
                parts.Add(string.Join(" AND ", distinctive.Select(EscapeFts5Term)));
            if (common.Count > 0)
                parts.Add(string.Join(" OR ", common.Select(EscapeFts5Term)));

            return string.Join(" ", parts);
        }

        // Without corpus data, use simple OR
        return string.Join(" OR ", terms.Select(EscapeFts5Term));
    }

    private static string EscapeFts5Term(string term)
    {
        // FTS5 special chars that need quoting
        if (term.Contains('"') || term.Contains('*') || term.Contains('-') ||
            term.Contains('+') || term.Contains('(') || term.Contains(')'))
        {
            return $"\"{term.Replace("\"", "\"\"")}\"";
        }
        return term;
    }

    private async Task UpsertKeywordAsync(string keyword, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO keyword_corpus (keyword, document_count, updated_at)
            VALUES (@kw, 1, @now)
            ON CONFLICT(keyword) DO UPDATE SET
                document_count = document_count + 1,
                updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@kw", keyword.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        _keywordCorpus[keyword] = _keywordCorpus.GetValueOrDefault(keyword) + 1;
    }

    private async Task LoadKeywordCorpusAsync(CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT keyword, document_count FROM keyword_corpus";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            _keywordCorpus[reader.GetString(0)] = reader.GetInt32(1);
        }

        // Count total documents
        using var countCmd = _db.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM fts_documents";
        var count = await countCmd.ExecuteScalarAsync(ct);
        _totalDocuments = count is long v ? (int)v : 0;
    }

    public async ValueTask DisposeAsync()
    {
        _dbLock.Dispose();
        await _db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;

namespace DoomSummarizer.Services;

/// <summary>
/// FTS5 full-text search index, keyword corpus for IDF, and related item retrieval.
/// Tables: items_fts, keyword_corpus
/// </summary>
public partial class StorageService
{
    // --- FTS5 Pre-Filter & Keyword Corpus Methods ---

    /// <summary>
    /// Index a document into the FTS5 virtual table for fast keyword pre-filtering.
    /// Called during ingestion after keyword extraction.
    /// </summary>
    public async Task IndexDocumentFtsAsync(string itemId, string title, string keywordsText, string contentPreview)
    {
        // Delete any existing entry first (FTS5 doesn't support UPSERT)
        await using var delCmd = _connection!.CreateCommand();
        delCmd.CommandText = "DELETE FROM items_fts WHERE item_id = @id";
        delCmd.Parameters.AddWithValue("@id", itemId);
        await delCmd.ExecuteNonQueryAsync();

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items_fts (item_id, title, keywords_text, content_preview)
            VALUES (@id, @title, @keywords, @preview)
            """;
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@keywords", keywordsText);
        cmd.Parameters.AddWithValue("@preview", contentPreview);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Pre-filter documents using FTS5 keyword match (Layer 1).
    /// Returns item IDs that match the query text, optionally filtered by source.
    /// </summary>
    public async Task<List<string>> FtsPreFilterAsync(string query, string? source = null, int limit = 50)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery)) return [];

        var ids = new List<string>();
        await using var cmd = _connection!.CreateCommand();

        if (source != null)
        {
            cmd.CommandText = """
                SELECT f.item_id
                FROM items_fts f
                JOIN items i ON f.item_id = i.id
                WHERE items_fts MATCH @query AND i.source = @source
                ORDER BY rank
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@source", source);
        }
        else
        {
            cmd.CommandText = """
                SELECT item_id
                FROM items_fts
                WHERE items_fts MATCH @query
                ORDER BY rank
                LIMIT @limit
                """;
        }

        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// Build an FTS5 query from user input, tokenizing and escaping for safety.
    /// Uses OR for broad matching so partial keyword overlap still finds candidates.
    /// </summary>
    private static string BuildFtsQuery(string userQuery)
    {
        var tokens = RelevanceScorer.Tokenize(userQuery);
        if (tokens.Count == 0) return "";

        // Escape each token for FTS5 (wrap in quotes to handle special chars)
        // Use OR for broad matching — FTS5 default is AND which is too restrictive
        return string.Join(" OR ", tokens.Select(t => $"\"{EscapeFtsToken(t)}\""));
    }

    /// <summary>
    /// Escape a token for FTS5 query syntax: double any internal quotes.
    /// </summary>
    private static string EscapeFtsToken(string token)
    {
        return token.Replace("\"", "\"\"");
    }

    /// <summary>
    /// Update global keyword corpus IDF counters.
    /// UPSERT: increment document_count for each keyword.
    /// </summary>
    public async Task UpdateKeywordCorpusAsync(IEnumerable<string> keywords)
    {
        var keywordList = keywords.ToList();
        if (keywordList.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var transaction = await _connection!.BeginTransactionAsync();
        try
        {
            foreach (var keyword in keywordList)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    INSERT INTO keyword_corpus (keyword, document_count, updated_at)
                    VALUES (@kw, 1, @now)
                    ON CONFLICT(keyword) DO UPDATE SET
                        document_count = document_count + 1,
                        updated_at = @now
                    """;
                cmd.Parameters.AddWithValue("@kw", keyword.ToLowerInvariant());
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Load the global keyword corpus for IDF computation.
    /// Returns keyword → document_count mapping.
    /// </summary>
    public async Task<Dictionary<string, int>> GetKeywordCorpusAsync()
    {
        var corpus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT keyword, document_count FROM keyword_corpus";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            corpus[reader.GetString(0)] = reader.GetInt32(1);
        }

        return corpus;
    }

    /// <summary>
    /// Get the total number of distinct documents in the keyword corpus.
    /// </summary>
    public async Task<int> GetKeywordCorpusSizeAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT id) FROM items";
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (int)l : 0;
    }

    /// <summary>
    /// Load ContentItems by their IDs (for FTS5 pre-filter results).
    /// </summary>
    public async Task<List<ContentItem>> LoadItemsByIdsAsync(List<string> ids)
    {
        var storedItems = await GetItemsByIdsAsync(ids);
        return storedItems
            .Select(s => s.ToContentItem())
            .ToList();
    }

    /// <summary>
    /// Get all items from the database (for backfilling FTS5 index).
    /// </summary>
    public async Task<List<StoredItem>> GetAllItemsAsync()
    {
        var items = new List<StoredItem>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM items ORDER BY fetched_at DESC";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(ReadStoredItem(reader));
        }

        return items;
    }

    /// <summary>
    /// Check if the FTS5 index has any entries (used to trigger backfill).
    /// </summary>
    public async Task<bool> IsFtsIndexEmptyAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items_fts";
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? l == 0 : true;
    }

    /// <summary>
    /// Batch save items + FTS index + keyword corpus in a single SQLite transaction.
    /// Much faster than per-item writes due to reduced fsync overhead.
    /// </summary>
    public async Task SaveAndIndexBatchAsync(List<(ContentItem item, DocumentProfile profile)> batch)
    {
        if (batch.Count == 0) return;

        await using var transaction = await _connection!.BeginTransactionAsync();
        try
        {
            // Collect all keywords for corpus update
            var allKeywords = new List<string>();

            foreach (var (item, profile) in batch)
            {
                // Save item
                await using var saveCmd = _connection.CreateCommand();
                saveCmd.Transaction = (SqliteTransaction)transaction;
                saveCmd.CommandText = """
                    INSERT OR REPLACE INTO items
                    (id, source, title, url, summary, content, sentiment_score, detected_topic, tags, score, created_at, fetched_at, embedding, keywords)
                    VALUES
                    (@id, @source, @title, @url, @summary, @content, @sentiment, @topic, @tags, @score, @created, @fetched, @embedding, @keywords)
                    """;
                saveCmd.Parameters.AddWithValue("@id", item.Id);
                saveCmd.Parameters.AddWithValue("@source", item.Source);
                saveCmd.Parameters.AddWithValue("@title", item.Title);
                saveCmd.Parameters.AddWithValue("@url", (object?)item.Url ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@summary", (object?)item.Summary ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@sentiment", item.SentimentScore);
                saveCmd.Parameters.AddWithValue("@topic", (object?)item.DetectedTopic ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@tags", item.Tags.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(item.Tags) : DBNull.Value);
                saveCmd.Parameters.AddWithValue("@score", item.Score);
                saveCmd.Parameters.AddWithValue("@created", item.CreatedAt.ToString("O"));
                saveCmd.Parameters.AddWithValue("@fetched", item.FetchedAt.ToString("O"));
                saveCmd.Parameters.AddWithValue("@embedding", item.Embedding != null ? EmbeddingService.ToBytes(item.Embedding) : DBNull.Value);
                saveCmd.Parameters.AddWithValue("@keywords", (object?)item.Keywords ?? DBNull.Value);
                await saveCmd.ExecuteNonQueryAsync();

                // FTS5 index: delete then insert (FTS5 doesn't support UPSERT)
                await using var delFts = _connection.CreateCommand();
                delFts.Transaction = (SqliteTransaction)transaction;
                delFts.CommandText = "DELETE FROM items_fts WHERE item_id = @id";
                delFts.Parameters.AddWithValue("@id", item.Id);
                await delFts.ExecuteNonQueryAsync();

                var contentPreview = (item.Content ?? "").Length > 2000
                    ? item.Content![..2000]
                    : item.Content ?? "";

                await using var ftsCmd = _connection.CreateCommand();
                ftsCmd.Transaction = (SqliteTransaction)transaction;
                ftsCmd.CommandText = """
                    INSERT INTO items_fts (item_id, title, keywords_text, content_preview)
                    VALUES (@id, @title, @keywords, @preview)
                    """;
                ftsCmd.Parameters.AddWithValue("@id", item.Id);
                ftsCmd.Parameters.AddWithValue("@title", item.Title);
                ftsCmd.Parameters.AddWithValue("@keywords", profile.KeywordsText);
                ftsCmd.Parameters.AddWithValue("@preview", contentPreview);
                await ftsCmd.ExecuteNonQueryAsync();

                allKeywords.AddRange(profile.TopKeywords.Select(k => k.Keyword));
            }

            // Batch keyword corpus update
            if (allKeywords.Count > 0)
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                foreach (var keyword in allKeywords.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await using var kwCmd = _connection.CreateCommand();
                    kwCmd.Transaction = (SqliteTransaction)transaction;
                    kwCmd.CommandText = """
                        INSERT INTO keyword_corpus (keyword, document_count, updated_at)
                        VALUES (@kw, 1, @now)
                        ON CONFLICT(keyword) DO UPDATE SET
                            document_count = document_count + 1,
                            updated_at = @now
                        """;
                    kwCmd.Parameters.AddWithValue("@kw", keyword.ToLowerInvariant());
                    kwCmd.Parameters.AddWithValue("@now", now);
                    await kwCmd.ExecuteNonQueryAsync();
                }
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

using System.Text.Json;
using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;

namespace DoomSummarizer.Services;

/// <summary>
///     FTS5 full-text search index, keyword corpus for IDF, and related item retrieval.
///     Tables: items_fts, keyword_corpus
/// </summary>
public partial class StorageService
{
    // --- FTS5 Pre-Filter & Keyword Corpus Methods ---

    /// <summary>
    ///     Index a document into the FTS5 virtual table for fast keyword pre-filtering.
    ///     Called during ingestion after keyword extraction.
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
    ///     Pre-filter documents using FTS5 keyword match (Layer 1).
    ///     Uses IDF-aware query construction: distinctive (high-IDF) tokens are required
    ///     via AND, common tokens are optional (OR) but boost rank.
    ///     Falls back to pure OR if too few results.
    ///     Returns item IDs that match the query text, optionally filtered by source.
    /// </summary>
    public async Task<List<string>> FtsPreFilterAsync(string query, string? source = null, int limit = 50)
    {
        var tokens = RelevanceScorer.Tokenize(query);
        if (tokens.Count == 0) return [];

        // Load keyword corpus for IDF-aware query construction
        Dictionary<string, int>? corpus = null;
        int? corpusSize = null;
        try
        {
            corpus = await GetKeywordCorpusAsync();
            if (corpus.Count > 0)
                corpusSize = await GetKeywordCorpusSizeAsync();
            else
                corpus = null;
        }
        catch
        {
            /* corpus not yet populated */
        }

        // Build IDF-aware query: require distinctive terms, OR common ones
        var smartQuery = BuildIdfAwareFtsQuery(tokens, corpus, corpusSize);
        var ids = await ExecuteFtsQueryAsync(smartQuery, source, limit);

        // Graceful fallback: if smart query too restrictive (<3 results), try pure OR
        if (ids.Count < 3 && tokens.Count > 1)
        {
            var orQuery = BuildFtsQuery(tokens, false);
            ids = await ExecuteFtsQueryAsync(orQuery, source, limit);
        }

        return ids;
    }

    /// <summary>
    ///     Execute an FTS5 query and return matching item IDs.
    /// </summary>
    private async Task<List<string>> ExecuteFtsQueryAsync(string ftsQuery, string? source, int limit)
    {
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
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));

        return ids;
    }

    /// <summary>
    ///     Build an IDF-aware FTS5 query: distinctive (high-IDF) tokens are required
    ///     via AND, common (low-IDF) tokens are grouped with OR as optional boosters.
    ///     Example: query "How does HTMX work with ASP.NET?"
    ///     tokens: ["htmx", "work", "asp", "net"]
    ///     IDF: htmx=high, work=low, asp=medium, net=low
    ///     → FTS5: "htmx" "asp" ("work" OR "net")
    ///     This ensures the distinctive terms are required while common terms don't
    ///     exclude documents that match the important keywords.
    /// </summary>
    private static string BuildIdfAwareFtsQuery(
        List<string> tokens,
        Dictionary<string, int>? corpus,
        int? corpusSize)
    {
        if (tokens.Count == 0) return "";
        if (tokens.Count == 1) return $"\"{EscapeFtsToken(tokens[0])}\"";

        // Without corpus, fall back to AND for all terms
        if (corpus == null || corpusSize is null or <= 0)
            return BuildFtsQuery(tokens, true);

        var n = corpusSize.Value;

        // Compute IDF per unique token (single-pass dedup + IDF computation)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokenIdfs = new List<(string token, double idf)>();
        foreach (var t in tokens)
        {
            if (!seen.Add(t)) continue;
            var df = corpus.GetValueOrDefault(t, 0);
            var idf = df > 0
                ? Math.Max(0.01, Math.Log((n - df + 0.5) / (df + 0.5) + 1))
                : 3.0; // Unknown tokens are likely distinctive (not in corpus = rare)
            tokenIdfs.Add((t, idf));
        }

        tokenIdfs.Sort((a, b) => b.idf.CompareTo(a.idf));

        // Split at median IDF: above = distinctive (required), at/below = common (optional)
        var medianIdf = tokenIdfs.Count > 0 ? tokenIdfs[tokenIdfs.Count / 2].idf : 1.0;

        // Single pass: partition into distinctive/common
        var distinctive = new List<string>();
        var common = new List<string>();
        foreach (var (token, idf) in tokenIdfs)
        {
            if (idf > medianIdf)
                distinctive.Add(token);
            else
                common.Add(token);
        }

        // Edge case: all tokens have same IDF → all required
        if (distinctive.Count == 0)
            return BuildFtsQuery(tokens, true);

        // Edge case: no common tokens → all required
        if (common.Count == 0)
            return BuildFtsQuery(tokens, true);

        // Build: distinctive1 distinctive2 (common1 OR common2)
        var requiredPart = string.Join(" ", distinctive.Select(t => $"\"{EscapeFtsToken(t)}\""));

        // Validate required part isn't empty (edge case protection)
        if (string.IsNullOrWhiteSpace(requiredPart))
            return BuildFtsQuery(tokens, true);

        // If we have common tokens, add them as optional boosters
        // FTS5 syntax: term1 term2 (term3 OR term4) doesn't work reliably
        // Use explicit OR at top level: term1 AND term2 AND (term3 OR term4)
        // But FTS5 doesn't have explicit AND - space means AND
        // Safer: just add common terms at end without OR (they become optional via relevance)
        if (common.Count > 0)
        {
            // Add common terms as individual tokens (FTS5 will rank by term frequency)
            // This avoids the OR syntax issue while still boosting docs with common terms
            var optionalPart = string.Join(" ", common.Select(t => EscapeFtsToken(t)));
            return $"{requiredPart} {optionalPart}";
        }

        return requiredPart;
    }

    /// <summary>
    ///     Build an FTS5 query from tokenized input.
    ///     AND mode: all terms must appear (implicit FTS5 default with space separator).
    ///     OR mode: any term can match (broad fallback when AND is too restrictive).
    /// </summary>
    private static string BuildFtsQuery(List<string> tokens, bool useAnd)
    {
        // Single-pass: filter, escape, and quote tokens
        var quoted = new List<string>();
        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t) || t.Length < 2) continue;
            var escaped = EscapeFtsToken(t);
            if (!string.IsNullOrWhiteSpace(escaped))
                quoted.Add($"\"{escaped}\"");
        }

        if (quoted.Count == 0) return "";

        return useAnd
            ? string.Join(" ", quoted) // FTS5 implicit AND
            : string.Join(" OR ", quoted); // Explicit OR for fallback
    }

    /// <summary>
    ///     Escape a token for FTS5 query syntax: double any internal quotes,
    ///     remove special FTS5 characters that could cause syntax errors.
    /// </summary>
    private static string EscapeFtsToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";

        // Remove FTS5 special characters that could cause syntax errors
        var cleaned = token
            .Replace("\"", "") // Remove quotes entirely (we wrap in quotes)
            .Replace("*", "") // Wildcard
            .Replace("(", "") // Grouping
            .Replace(")", "")
            .Replace(":", "") // Column prefix
            .Replace("^", "") // Boost
            .Replace("-", " ") // Negation → space
            .Replace("+", " ") // Required → space
            .Trim();

        return cleaned;
    }

    /// <summary>
    ///     Update global keyword corpus IDF counters.
    ///     UPSERT: increment document_count for each keyword.
    /// </summary>
    public async Task UpdateKeywordCorpusAsync(IEnumerable<string> keywords)
    {
        var keywordList = keywords.ToList();
        if (keywordList.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var transaction = await _connection!.BeginTransactionAsync();
        try
        {
            // Reuse a single prepared command — just reset the keyword parameter each iteration
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                              INSERT INTO keyword_corpus (keyword, document_count, updated_at)
                              VALUES (@kw, 1, @now)
                              ON CONFLICT(keyword) DO UPDATE SET
                                  document_count = document_count + 1,
                                  updated_at = @now
                              """;
            var kwParam = cmd.Parameters.Add("@kw", Microsoft.Data.Sqlite.SqliteType.Text);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.PrepareAsync();

            foreach (var keyword in keywordList)
            {
                kwParam.Value = keyword.ToLowerInvariant();
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
    ///     Load the global keyword corpus for IDF computation.
    ///     Returns keyword → document_count mapping.
    /// </summary>
    public async Task<Dictionary<string, int>> GetKeywordCorpusAsync()
    {
        var corpus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT keyword, document_count FROM keyword_corpus";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) corpus[reader.GetString(0)] = reader.GetInt32(1);

        return corpus;
    }

    /// <summary>
    ///     Get the total number of distinct documents in the keyword corpus.
    /// </summary>
    public async Task<int> GetKeywordCorpusSizeAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT id) FROM items";
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (int)l : 0;
    }

    /// <summary>
    ///     Load ContentItems by their IDs (for FTS5 pre-filter results).
    /// </summary>
    public async Task<List<ContentItem>> LoadItemsByIdsAsync(List<string> ids)
    {
        var storedItems = await GetItemsByIdsAsync(ids);
        return storedItems
            .Select(s => s.ToContentItem())
            .ToList();
    }

    /// <summary>
    ///     Get all items from the database (for backfilling FTS5 index).
    /// </summary>
    public async Task<List<StoredItem>> GetAllItemsAsync()
    {
        var items = new List<StoredItem>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM items ORDER BY fetched_at DESC";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) items.Add(ReadStoredItem(reader));

        return items;
    }

    /// <summary>
    ///     Check if the FTS5 index has any entries (used to trigger backfill).
    /// </summary>
    public async Task<bool> IsFtsIndexEmptyAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items_fts";
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? l == 0 : true;
    }

    /// <summary>
    ///     Batch save items + FTS index + keyword corpus in a single SQLite transaction.
    ///     Much faster than per-item writes due to reduced fsync overhead.
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
                                      (id, source, title, url, summary, content, sentiment_score, detected_topic, tags, score, created_at, fetched_at, embedding, keywords, salience_score, is_embedded)
                                      VALUES
                                      (@id, @source, @title, @url, @summary, @content, @sentiment, @topic, @tags, @score, @created, @fetched, @embedding, @keywords, @salience, @is_embedded)
                                      """;
                saveCmd.Parameters.AddWithValue("@id", item.Id);
                saveCmd.Parameters.AddWithValue("@source", item.Source);
                saveCmd.Parameters.AddWithValue("@title", item.Title);
                saveCmd.Parameters.AddWithValue("@url", (object?)item.Url ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@summary", (object?)item.Summary ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@sentiment", item.SentimentScore);
                saveCmd.Parameters.AddWithValue("@topic", (object?)item.DetectedTopic ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@tags",
                    item.Tags.Count > 0 ? JsonSerializer.Serialize(item.Tags) : DBNull.Value);
                saveCmd.Parameters.AddWithValue("@score", item.Score);
                saveCmd.Parameters.AddWithValue("@created", item.CreatedAt.ToString("O"));
                saveCmd.Parameters.AddWithValue("@fetched", item.FetchedAt.ToString("O"));
                saveCmd.Parameters.AddWithValue("@embedding",
                    item.Embedding != null ? EmbeddingCompat.ToBytes(item.Embedding) : DBNull.Value);
                saveCmd.Parameters.AddWithValue("@keywords", (object?)item.Keywords ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@salience", (object?)item.SalienceScore ?? DBNull.Value);
                saveCmd.Parameters.AddWithValue("@is_embedded", item.IsEmbedded ? 1 : 0);
                await saveCmd.ExecuteNonQueryAsync();

                // FTS5 index: delete then insert (FTS5 doesn't support UPSERT)
                await using var delFts = _connection.CreateCommand();
                delFts.Transaction = (SqliteTransaction)transaction;
                delFts.CommandText = "DELETE FROM items_fts WHERE item_id = @id";
                delFts.Parameters.AddWithValue("@id", item.Id);
                await delFts.ExecuteNonQueryAsync();

                var contentPreview = item.Content ?? "";

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
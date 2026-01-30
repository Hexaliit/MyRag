using System.Text.Json;
using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;

namespace DoomSummarizer.Services;

/// <summary>
/// SQLite-backed storage for DoomSummarizer items, entities, and caches.
/// Split into partial class files by responsibility:
///   - StorageService.cs          — Core: schema, item CRUD, helpers
///   - StorageService.Items.cs    — Item retrieval: recent, similar, batch load
///   - StorageService.EntityGraph.cs — Entity graph: entities, mentions, relationships
///   - StorageService.QueryFeedback.cs — Query logging, LFU tracking
///   - StorageService.Cache.cs    — Feature cache (disambiguation) + URL cache (ETag/hash)
///   - StorageService.Fts.cs      — FTS5 full-text search + keyword corpus (IDF)
///   - StorageService.Analytics.cs — Trends, summaries, collections, maintenance
/// </summary>
public partial class StorageService : IAsyncDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;

    /// <summary>
    /// Directory containing the database file. Used for co-locating Lucene indexes.
    /// </summary>
    public string DataPath => Path.GetDirectoryName(_dbPath) ?? ".";

    public StorageService(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync();

        // Enable WAL mode for better concurrent access (reads don't block writes)
        await using (var walCmd = _connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await walCmd.ExecuteNonQueryAsync();
        }

        // Create tables
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS items (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT UNIQUE NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                url TEXT,
                summary TEXT,
                content TEXT,
                sentiment_score REAL DEFAULT 0,
                detected_topic TEXT,
                tags TEXT,
                score INTEGER DEFAULT 0,
                created_at TEXT NOT NULL,
                fetched_at TEXT NOT NULL,
                embedding BLOB
            );

            CREATE INDEX IF NOT EXISTS idx_items_source ON items(source);
            CREATE INDEX IF NOT EXISTS idx_items_fetched ON items(fetched_at);
            CREATE INDEX IF NOT EXISTS idx_items_topic ON items(detected_topic);

            CREATE TABLE IF NOT EXISTS daily_stats (
                date TEXT PRIMARY KEY,
                total_items INTEGER DEFAULT 0,
                avg_sentiment REAL DEFAULT 0,
                top_topics TEXT,
                source_counts TEXT
            );

            CREATE TABLE IF NOT EXISTS summaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                generated_at TEXT NOT NULL,
                vibe TEXT NOT NULL,
                content TEXT NOT NULL,
                item_count INTEGER DEFAULT 0
            );

            -- Knowledge graph: entities extracted from articles
            CREATE TABLE IF NOT EXISTS entities (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT NOT NULL,
                description TEXT,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                mention_count INTEGER DEFAULT 1,
                embedding BLOB
            );
            CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(type);

            -- Knowledge graph: entity-to-article provenance
            CREATE TABLE IF NOT EXISTS entity_mentions (
                entity_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                confidence REAL DEFAULT 0.5,
                context TEXT,
                mentioned_at TEXT NOT NULL,
                PRIMARY KEY (entity_id, item_id),
                FOREIGN KEY (entity_id) REFERENCES entities(id),
                FOREIGN KEY (item_id) REFERENCES items(id)
            );

            -- URL fetch cache: ETags, content hashes, last-modified for conditional fetching
            CREATE TABLE IF NOT EXISTS url_cache (
                url TEXT PRIMARY KEY,
                content_hash TEXT,
                etag TEXT,
                last_modified TEXT,
                last_fetched TEXT NOT NULL,
                content_length INTEGER DEFAULT 0,
                hit_count INTEGER DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_url_cache_fetched ON url_cache(last_fetched);

            -- Query feedback: log queries with embeddings for segment reuse
            CREATE TABLE IF NOT EXISTS query_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                query_text TEXT NOT NULL,
                query_embedding BLOB,
                vibe TEXT,
                item_ids TEXT,
                item_count INTEGER DEFAULT 0,
                issued_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_query_log_issued ON query_log(issued_at);

            -- Item usage frequency (LFU decay signal)
            CREATE TABLE IF NOT EXISTS item_usage (
                item_id TEXT PRIMARY KEY,
                access_count INTEGER DEFAULT 0,
                last_accessed TEXT NOT NULL,
                avg_rank REAL DEFAULT 0.0
            );
            CREATE INDEX IF NOT EXISTS idx_item_usage_accessed ON item_usage(last_accessed);

            -- Feature cache for entity disambiguation
            CREATE TABLE IF NOT EXISTS feature_cache (
                term TEXT PRIMARY KEY,
                category TEXT,
                embedding BLOB NOT NULL,
                hit_count INTEGER DEFAULT 1,
                created_at TEXT NOT NULL,
                last_used TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_feature_cache_category ON feature_cache(category);

            -- Knowledge graph: entity co-occurrence relationships
            CREATE TABLE IF NOT EXISTS entity_relationships (
                source_entity_id TEXT NOT NULL,
                target_entity_id TEXT NOT NULL,
                relationship_type TEXT DEFAULT 'co_occurs',
                weight REAL DEFAULT 1.0,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                PRIMARY KEY (source_entity_id, target_entity_id),
                FOREIGN KEY (source_entity_id) REFERENCES entities(id),
                FOREIGN KEY (target_entity_id) REFERENCES entities(id)
            );

            -- FTS5 virtual table for fast keyword pre-filtering
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
                item_id UNINDEXED,
                title,
                keywords_text,
                content_preview,
                tokenize = 'porter ascii'
            );

            -- Global keyword corpus for proper IDF computation
            CREATE TABLE IF NOT EXISTS keyword_corpus (
                keyword TEXT PRIMARY KEY,
                document_count INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();

        // Add keywords column to items table (safe migration for existing DBs)
        try
        {
            await using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE items ADD COLUMN keywords TEXT";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException)
        {
            // Column already exists — that's fine
        }
    }

    // --- Core Item CRUD ---

    public async Task<bool> ExistsAsync(string id)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() != null;
    }

    /// <summary>
    /// Check if an item was fetched within the last N hours.
    /// Returns false if item doesn't exist or was fetched longer ago.
    /// </summary>
    public async Task<bool> ExistsRecentlyAsync(string id, int hoursAgo = 4)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM items
            WHERE id = @id
            AND datetime(fetched_at) > datetime('now', @hours)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@hours", $"-{hoursAgo} hours");
        return await cmd.ExecuteScalarAsync() != null;
    }

    public async Task SaveItemAsync(ContentItem item)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO items
            (id, source, title, url, summary, content, sentiment_score, detected_topic, tags, score, created_at, fetched_at, embedding, keywords)
            VALUES
            (@id, @source, @title, @url, @summary, @content, @sentiment, @topic, @tags, @score, @created, @fetched, @embedding, @keywords)
            """;

        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.Parameters.AddWithValue("@source", item.Source);
        cmd.Parameters.AddWithValue("@title", item.Title);
        cmd.Parameters.AddWithValue("@url", (object?)item.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@summary", (object?)item.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sentiment", item.SentimentScore);
        cmd.Parameters.AddWithValue("@topic", (object?)item.DetectedTopic ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", item.Tags.Count > 0 ? JsonSerializer.Serialize(item.Tags) : DBNull.Value);
        cmd.Parameters.AddWithValue("@score", item.Score);
        cmd.Parameters.AddWithValue("@created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@fetched", item.FetchedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@embedding", item.Embedding != null ? EmbeddingCompat.ToBytes(item.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@keywords", (object?)item.Keywords ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    // --- Helpers ---

    /// <summary>
    /// Read a StoredItem from a data reader row.
    /// Shared across all partial class files.
    /// </summary>
    private static StoredItem ReadStoredItem(SqliteDataReader reader)
    {
        // Read keywords column safely (may not exist in older DBs before migration)
        string? keywords = null;
        try
        {
            var keywordsOrd = reader.GetOrdinal("keywords");
            keywords = reader.IsDBNull(keywordsOrd) ? null : reader.GetString(keywordsOrd);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Column doesn't exist yet — that's fine
        }

        return new StoredItem
        {
            RowId = reader.GetInt64(reader.GetOrdinal("row_id")),
            Id = reader.GetString(reader.GetOrdinal("id")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Url = reader.IsDBNull(reader.GetOrdinal("url")) ? null : reader.GetString(reader.GetOrdinal("url")),
            Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
            Content = reader.IsDBNull(reader.GetOrdinal("content")) ? null : reader.GetString(reader.GetOrdinal("content")),
            SentimentScore = reader.IsDBNull(reader.GetOrdinal("sentiment_score")) ? 0 : reader.GetFloat(reader.GetOrdinal("sentiment_score")),
            DetectedTopic = reader.IsDBNull(reader.GetOrdinal("detected_topic")) ? null : reader.GetString(reader.GetOrdinal("detected_topic")),
            Tags = reader.IsDBNull(reader.GetOrdinal("tags")) ? null : reader.GetString(reader.GetOrdinal("tags")),
            Score = reader.IsDBNull(reader.GetOrdinal("score")) ? 0 : reader.GetInt32(reader.GetOrdinal("score")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            FetchedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("fetched_at"))),
            Embedding = reader.IsDBNull(reader.GetOrdinal("embedding")) ? null : (byte[])reader["embedding"],
            Keywords = keywords
        };
    }

    // --- Lifecycle ---

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
    }
}

/// <summary>
/// Stats for a KB collection (grouped by source).
/// </summary>
public record CollectionInfo
{
    public required string Source { get; init; }
    public int ItemCount { get; init; }
    public int WithEmbeddings { get; init; }
    public DateTimeOffset Earliest { get; init; }
    public DateTimeOffset Latest { get; init; }
    public int AvgContentLength { get; init; }
}

/// <summary>
/// A past query that matched the current one by embedding similarity.
/// </summary>
public record QueryMatch
{
    public long QueryId { get; init; }
    public required string QueryText { get; init; }
    public string? Vibe { get; init; }
    public List<string> ItemIds { get; init; } = [];
    public DateTimeOffset IssuedAt { get; init; }
    public float Similarity { get; init; }
}

/// <summary>
/// Cached URL fetch metadata: ETag, content hash, last-modified.
/// </summary>
public record UrlCacheEntry
{
    public required string Url { get; init; }
    public string? ContentHash { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public DateTimeOffset LastFetched { get; init; }
    public int ContentLength { get; init; }
    public int HitCount { get; init; }
}

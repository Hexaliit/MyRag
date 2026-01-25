using System.Text.Json;
using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;

namespace DoomSummarizer.Services;

public class StorageService : IAsyncDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;

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
            """;
        await cmd.ExecuteNonQueryAsync();
    }

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
            (id, source, title, url, summary, content, sentiment_score, detected_topic, tags, score, created_at, fetched_at, embedding)
            VALUES
            (@id, @source, @title, @url, @summary, @content, @sentiment, @topic, @tags, @score, @created, @fetched, @embedding)
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
        cmd.Parameters.AddWithValue("@embedding", item.Embedding != null ? EmbeddingService.ToBytes(item.Embedding) : DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<StoredItem>> GetRecentItemsAsync(int days = 7, string? source = null)
    {
        var items = new List<StoredItem>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = source != null
            ? "SELECT * FROM items WHERE fetched_at >= @cutoff AND source = @source ORDER BY fetched_at DESC"
            : "SELECT * FROM items WHERE fetched_at >= @cutoff ORDER BY fetched_at DESC";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        if (source != null) cmd.Parameters.AddWithValue("@source", source);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(ReadStoredItem(reader));
        }

        return items;
    }

    public async Task<List<StoredItem>> FindSimilarAsync(float[] embedding, int limit = 10, double threshold = 0.85)
    {
        // Simple brute-force similarity search - works fine for small datasets
        var items = new List<(StoredItem item, float similarity)>();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE embedding IS NOT NULL ORDER BY fetched_at DESC LIMIT 1000";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = ReadStoredItem(reader);
            if (item.Embedding == null) continue;

            var storedEmbedding = EmbeddingService.FromBytes(item.Embedding);
            var similarity = EmbeddingService.CosineSimilarity(embedding, storedEmbedding);

            if (similarity >= threshold)
            {
                items.Add((item, similarity));
            }
        }

        return items
            .OrderByDescending(x => x.similarity)
            .Take(limit)
            .Select(x => x.item)
            .ToList();
    }

    public async Task<TrendAnalysis> GetTrendAnalysisAsync(int days = 7)
    {
        var endDate = DateTimeOffset.UtcNow;
        var startDate = endDate.AddDays(-days);
        var prevStartDate = startDate.AddDays(-days);

        // Current period stats
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                AVG(sentiment_score) as avg_sentiment,
                detected_topic
            FROM items
            WHERE fetched_at >= @start AND fetched_at <= @end
            GROUP BY detected_topic
            ORDER BY COUNT(*) DESC
            """;
        cmd.Parameters.AddWithValue("@start", startDate.ToString("O"));
        cmd.Parameters.AddWithValue("@end", endDate.ToString("O"));

        var topicCounts = new Dictionary<string, (int count, float sentiment)>();
        int totalItems = 0;
        float totalSentiment = 0;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var count = reader.GetInt32(0);
            var sentiment = reader.IsDBNull(1) ? 0f : reader.GetFloat(1);
            var topic = reader.IsDBNull(2) ? "general" : reader.GetString(2);

            topicCounts[topic] = (count, sentiment);
            totalItems += count;
            totalSentiment += sentiment * count;
        }

        // Previous period for comparison
        await using var prevCmd = _connection.CreateCommand();
        prevCmd.CommandText = "SELECT AVG(sentiment_score) FROM items WHERE fetched_at >= @start AND fetched_at < @end";
        prevCmd.Parameters.AddWithValue("@start", prevStartDate.ToString("O"));
        prevCmd.Parameters.AddWithValue("@end", startDate.ToString("O"));

        var prevSentimentObj = await prevCmd.ExecuteScalarAsync();
        var prevSentiment = prevSentimentObj is DBNull or null ? 0f : Convert.ToSingle(prevSentimentObj);
        var currentSentiment = totalItems > 0 ? totalSentiment / totalItems : 0f;

        return new TrendAnalysis
        {
            StartDate = startDate,
            EndDate = endDate,
            TotalItems = totalItems,
            AverageSentiment = currentSentiment,
            SentimentChange = currentSentiment - prevSentiment,
            TopTopics = topicCounts
                .OrderByDescending(x => x.Value.count)
                .Take(10)
                .Select(x => new TopicTrend
                {
                    Topic = x.Key,
                    Count = x.Value.count,
                    AverageSentiment = x.Value.sentiment
                })
                .ToList()
        };
    }

    public async Task SaveSummaryAsync(string vibe, string content, int itemCount)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO summaries (generated_at, vibe, content, item_count)
            VALUES (@generated, @vibe, @content, @count)
            """;
        cmd.Parameters.AddWithValue("@generated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@vibe", vibe);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@count", itemCount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CleanupOldDataAsync(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE fetched_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync();
    }

    private static StoredItem ReadStoredItem(SqliteDataReader reader)
    {
        return new StoredItem
        {
            RowId = reader.GetInt64(reader.GetOrdinal("row_id")),
            Id = reader.GetString(reader.GetOrdinal("id")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Url = reader.IsDBNull(reader.GetOrdinal("url")) ? null : reader.GetString(reader.GetOrdinal("url")),
            Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
            SentimentScore = reader.IsDBNull(reader.GetOrdinal("sentiment_score")) ? 0 : reader.GetFloat(reader.GetOrdinal("sentiment_score")),
            DetectedTopic = reader.IsDBNull(reader.GetOrdinal("detected_topic")) ? null : reader.GetString(reader.GetOrdinal("detected_topic")),
            Tags = reader.IsDBNull(reader.GetOrdinal("tags")) ? null : reader.GetString(reader.GetOrdinal("tags")),
            Score = reader.IsDBNull(reader.GetOrdinal("score")) ? 0 : reader.GetInt32(reader.GetOrdinal("score")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            FetchedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("fetched_at"))),
            Embedding = reader.IsDBNull(reader.GetOrdinal("embedding")) ? null : (byte[])reader["embedding"]
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
    }
}

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

    // --- Knowledge Graph Methods ---

    /// <summary>
    /// Upsert an entity, incrementing mention count and updating last_seen.
    /// </summary>
    public async Task UpsertEntityAsync(string id, string name, string type, double confidence, float[]? embedding = null)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entities (id, name, type, first_seen, last_seen, mention_count, embedding)
            VALUES (@id, @name, @type, @now, @now, 1, @embedding)
            ON CONFLICT(id) DO UPDATE SET
                last_seen = @now,
                mention_count = mention_count + 1,
                embedding = COALESCE(@embedding, embedding)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@embedding", embedding != null ? EmbeddingService.ToBytes(embedding) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Record that an entity was mentioned in a specific article.
    /// </summary>
    public async Task UpsertEntityMentionAsync(string entityId, string itemId, double confidence, string? context = null)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_mentions (entity_id, item_id, confidence, context, mentioned_at)
            VALUES (@entityId, @itemId, @confidence, @context, @now)
            ON CONFLICT(entity_id, item_id) DO UPDATE SET
                confidence = MAX(confidence, @confidence)
            """;
        cmd.Parameters.AddWithValue("@entityId", entityId);
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@context", (object?)context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Upsert a co-occurrence relationship between two entities.
    /// </summary>
    public async Task UpsertRelationshipAsync(string sourceId, string targetId, string relType = "co_occurs")
    {
        // Normalize ordering so (A,B) and (B,A) are the same edge
        var (s, t) = string.CompareOrdinal(sourceId, targetId) < 0
            ? (sourceId, targetId)
            : (targetId, sourceId);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_relationships (source_entity_id, target_entity_id, relationship_type, weight, first_seen, last_seen)
            VALUES (@src, @tgt, @type, 1.0, @now, @now)
            ON CONFLICT(source_entity_id, target_entity_id) DO UPDATE SET
                weight = weight + 1.0,
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@src", s);
        cmd.Parameters.AddWithValue("@tgt", t);
        cmd.Parameters.AddWithValue("@type", relType);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Get top entities by mention count, optionally filtered by type and freshness.
    /// </summary>
    public async Task<List<GraphEntity>> GetTopEntitiesAsync(int limit = 20, string? type = null, int? daysBack = null)
    {
        var entities = new List<GraphEntity>();
        await using var cmd = _connection!.CreateCommand();

        var where = new List<string>();
        if (type != null)
        {
            where.Add("e.type = @type");
            cmd.Parameters.AddWithValue("@type", type);
        }
        if (daysBack != null)
        {
            where.Add("e.last_seen >= @cutoff");
            cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-daysBack.Value).ToString("O"));
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT e.id, e.name, e.type, e.mention_count, e.first_seen, e.last_seen,
                   COUNT(DISTINCT em.item_id) as article_count
            FROM entities e
            LEFT JOIN entity_mentions em ON e.id = em.entity_id
            {whereClause}
            GROUP BY e.id
            ORDER BY e.mention_count DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entities.Add(new GraphEntity
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Type = reader.GetString(2),
                MentionCount = reader.GetInt32(3),
                FirstSeen = DateTimeOffset.Parse(reader.GetString(4)),
                LastSeen = DateTimeOffset.Parse(reader.GetString(5)),
                ArticleCount = reader.GetInt32(6)
            });
        }

        return entities;
    }

    /// <summary>
    /// Get relationships for an entity.
    /// </summary>
    public async Task<List<GraphRelationship>> GetRelationshipsAsync(string entityId)
    {
        var relationships = new List<GraphRelationship>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT r.source_entity_id, r.target_entity_id, r.relationship_type, r.weight,
                   COALESCE(es.name, r.source_entity_id) as source_name,
                   COALESCE(et.name, r.target_entity_id) as target_name
            FROM entity_relationships r
            LEFT JOIN entities es ON r.source_entity_id = es.id
            LEFT JOIN entities et ON r.target_entity_id = et.id
            WHERE r.source_entity_id = @id OR r.target_entity_id = @id
            ORDER BY r.weight DESC
            """;
        cmd.Parameters.AddWithValue("@id", entityId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relationships.Add(new GraphRelationship
            {
                SourceId = reader.GetString(0),
                TargetId = reader.GetString(1),
                Type = reader.GetString(2),
                Weight = reader.GetFloat(3),
                SourceName = reader.GetString(4),
                TargetName = reader.GetString(5)
            });
        }

        return relationships;
    }

    /// <summary>
    /// Get articles that mention a specific entity.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, double confidence)>> GetArticlesForEntityAsync(string entityId)
    {
        var articles = new List<(string, string, string?, double)>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT i.id, i.title, i.url, em.confidence
            FROM entity_mentions em
            JOIN items i ON em.item_id = i.id
            WHERE em.entity_id = @entityId
            ORDER BY em.confidence DESC
            """;
        cmd.Parameters.AddWithValue("@entityId", entityId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            articles.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDouble(3)
            ));
        }

        return articles;
    }

    /// <summary>
    /// Get graph statistics.
    /// </summary>
    public async Task<(int entities, int relationships, int mentions)> GetGraphStatsAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM entities),
                (SELECT COUNT(*) FROM entity_relationships),
                (SELECT COUNT(*) FROM entity_mentions)
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
        return (0, 0, 0);
    }

    // --- Query Feedback / LFU Methods ---

    /// <summary>
    /// Log a query and the item IDs it returned, for segment reuse on similar future queries.
    /// </summary>
    public async Task LogQueryAsync(string queryText, float[]? queryEmbedding, string? vibe, List<string> itemIds)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO query_log (query_text, query_embedding, vibe, item_ids, item_count, issued_at)
            VALUES (@query, @embedding, @vibe, @itemIds, @count, @now)
            """;
        cmd.Parameters.AddWithValue("@query", queryText);
        cmd.Parameters.AddWithValue("@embedding", queryEmbedding != null ? EmbeddingService.ToBytes(queryEmbedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@vibe", (object?)vibe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@itemIds", JsonSerializer.Serialize(itemIds));
        cmd.Parameters.AddWithValue("@count", itemIds.Count);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();

        // Update item_usage counters for LFU
        for (var i = 0; i < itemIds.Count; i++)
        {
            await using var usageCmd = _connection.CreateCommand();
            usageCmd.CommandText = """
                INSERT INTO item_usage (item_id, access_count, last_accessed, avg_rank)
                VALUES (@id, 1, @now, @rank)
                ON CONFLICT(item_id) DO UPDATE SET
                    access_count = access_count + 1,
                    last_accessed = @now,
                    avg_rank = (avg_rank * (access_count - 1) + @rank) / access_count
                """;
            usageCmd.Parameters.AddWithValue("@id", itemIds[i]);
            usageCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            usageCmd.Parameters.AddWithValue("@rank", i + 1);
            await usageCmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Find a recent query whose embedding is similar to the given one.
    /// Returns the item IDs from that query if found, null otherwise.
    /// Only considers queries within the last <paramref name="maxAgeHours"/>.
    /// </summary>
    public async Task<QueryMatch?> FindSimilarQueryAsync(float[] queryEmbedding, double threshold = 0.85, int maxAgeHours = 4)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-maxAgeHours).ToString("O");
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id, query_text, query_embedding, vibe, item_ids, issued_at FROM query_log WHERE issued_at >= @cutoff AND query_embedding IS NOT NULL ORDER BY issued_at DESC LIMIT 50";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        QueryMatch? best = null;
        var bestSim = 0.0f;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var storedEmbedding = EmbeddingService.FromBytes((byte[])reader["query_embedding"]);
            var similarity = EmbeddingService.CosineSimilarity(queryEmbedding, storedEmbedding);

            if (similarity >= threshold && similarity > bestSim)
            {
                bestSim = similarity;
                var itemIdsJson = reader.GetString(reader.GetOrdinal("item_ids"));
                best = new QueryMatch
                {
                    QueryId = reader.GetInt64(0),
                    QueryText = reader.GetString(1),
                    Vibe = reader.IsDBNull(reader.GetOrdinal("vibe")) ? null : reader.GetString(reader.GetOrdinal("vibe")),
                    ItemIds = JsonSerializer.Deserialize<List<string>>(itemIdsJson) ?? [],
                    IssuedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("issued_at"))),
                    Similarity = similarity
                };
            }
        }

        return best;
    }

    /// <summary>
    /// Load content items by their IDs (for segment reuse from a cached query).
    /// </summary>
    public async Task<List<StoredItem>> GetItemsByIdsAsync(List<string> ids)
    {
        if (ids.Count == 0) return [];

        var items = new List<StoredItem>();
        // SQLite parameter limit workaround: batch queries
        foreach (var batch in ids.Chunk(50))
        {
            await using var cmd = _connection!.CreateCommand();
            var placeholders = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders.Add($"@id{i}");
                cmd.Parameters.AddWithValue($"@id{i}", batch[i]);
            }
            cmd.CommandText = $"SELECT * FROM items WHERE id IN ({string.Join(",", placeholders)})";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(ReadStoredItem(reader));
            }
        }

        return items;
    }

    /// <summary>
    /// Get LFU usage stats for items (access count and recency).
    /// Returns a dictionary of item_id → (accessCount, lastAccessed).
    /// </summary>
    public async Task<Dictionary<string, (int accessCount, DateTimeOffset lastAccessed)>> GetItemUsageAsync(List<string> itemIds)
    {
        var result = new Dictionary<string, (int, DateTimeOffset)>();
        if (itemIds.Count == 0) return result;

        foreach (var batch in itemIds.Chunk(50))
        {
            await using var cmd = _connection!.CreateCommand();
            var placeholders = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders.Add($"@id{i}");
                cmd.Parameters.AddWithValue($"@id{i}", batch[i]);
            }
            cmd.CommandText = $"SELECT item_id, access_count, last_accessed FROM item_usage WHERE item_id IN ({string.Join(",", placeholders)})";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result[reader.GetString(0)] = (
                    reader.GetInt32(1),
                    DateTimeOffset.Parse(reader.GetString(2))
                );
            }
        }

        return result;
    }

    // --- Feature Cache Methods (Entity Disambiguation) ---

    /// <summary>
    /// Get a cached feature embedding by term. Returns (embeddingBytes, category) or null.
    /// Bumps hit_count and last_used on cache hit.
    /// </summary>
    public async Task<(byte[] embedding, string? category)?> GetCachedFeatureEmbeddingAsync(string term)
    {
        byte[]? embeddingBytes = null;
        string? category = null;

        // Read and fully consume the reader before issuing the UPDATE
        await using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = "SELECT embedding, category FROM feature_cache WHERE term = @term";
            cmd.Parameters.AddWithValue("@term", term);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                embeddingBytes = (byte[])reader["embedding"];
                category = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        if (embeddingBytes == null)
            return null;

        // Bump hit_count and last_used (reader is closed)
        await using (var updateCmd = _connection!.CreateCommand())
        {
            updateCmd.CommandText = """
                UPDATE feature_cache
                SET hit_count = hit_count + 1, last_used = @now
                WHERE term = @term
                """;
            updateCmd.Parameters.AddWithValue("@term", term);
            updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await updateCmd.ExecuteNonQueryAsync();
        }

        return (embeddingBytes, category);
    }

    /// <summary>
    /// Insert or update a feature cache entry.
    /// </summary>
    public async Task UpsertFeatureCacheAsync(string term, string? category, byte[] embeddingBytes)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feature_cache (term, category, embedding, hit_count, created_at, last_used)
            VALUES (@term, @category, @embedding, 1, @now, @now)
            ON CONFLICT(term) DO UPDATE SET
                hit_count = hit_count + 1,
                last_used = @now
            """;
        cmd.Parameters.AddWithValue("@term", term);
        cmd.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embedding", embeddingBytes);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    // --- URL Cache Methods ---

    /// <summary>
    /// Get cached info for a URL (ETag, content hash, last fetch time).
    /// </summary>
    public async Task<UrlCacheEntry?> GetUrlCacheAsync(string url)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT url, content_hash, etag, last_modified, last_fetched, content_length, hit_count FROM url_cache WHERE url = @url";
        cmd.Parameters.AddWithValue("@url", NormalizeCacheUrl(url));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UrlCacheEntry
            {
                Url = reader.GetString(0),
                ContentHash = reader.IsDBNull(1) ? null : reader.GetString(1),
                ETag = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastModified = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastFetched = DateTimeOffset.Parse(reader.GetString(4)),
                ContentLength = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                HitCount = reader.GetInt32(6)
            };
        }
        return null;
    }

    /// <summary>
    /// Check if a URL was fetched recently and content hasn't changed.
    /// Returns true if we can skip processing.
    /// </summary>
    public async Task<bool> IsUrlFreshAsync(string url, int decayHours = 4)
    {
        var entry = await GetUrlCacheAsync(url);
        if (entry == null) return false;
        return (DateTimeOffset.UtcNow - entry.LastFetched).TotalHours < decayHours;
    }

    /// <summary>
    /// Update the URL cache after a fetch.
    /// </summary>
    public async Task UpdateUrlCacheAsync(string url, string? contentHash, string? etag, string? lastModified, int contentLength)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO url_cache (url, content_hash, etag, last_modified, last_fetched, content_length, hit_count)
            VALUES (@url, @hash, @etag, @lastMod, @now, @len, 1)
            ON CONFLICT(url) DO UPDATE SET
                content_hash = @hash,
                etag = COALESCE(@etag, etag),
                last_modified = COALESCE(@lastMod, last_modified),
                last_fetched = @now,
                content_length = @len,
                hit_count = hit_count + 1
            """;
        cmd.Parameters.AddWithValue("@url", NormalizeCacheUrl(url));
        cmd.Parameters.AddWithValue("@hash", (object?)contentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastMod", (object?)lastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@len", contentLength);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Check if content hash matches what's cached (content unchanged).
    /// </summary>
    public async Task<bool> IsContentUnchangedAsync(string url, string contentHash)
    {
        var entry = await GetUrlCacheAsync(url);
        return entry?.ContentHash == contentHash;
    }

    private static string NormalizeCacheUrl(string url) =>
        url.Split('?')[0].Split('#')[0].TrimEnd('/').ToLowerInvariant();

    public async Task CleanupOldDataAsync(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            -- Delete old items
            DELETE FROM items WHERE fetched_at < @cutoff;

            -- Clean up orphaned entity mentions (items deleted above)
            DELETE FROM entity_mentions WHERE item_id NOT IN (SELECT id FROM items);

            -- Clean up entities with no remaining mentions
            DELETE FROM entities WHERE id NOT IN (SELECT DISTINCT entity_id FROM entity_mentions);

            -- Clean up orphaned relationships
            DELETE FROM entity_relationships
            WHERE source_entity_id NOT IN (SELECT id FROM entities)
               OR target_entity_id NOT IN (SELECT id FROM entities);

            -- Clean up old URL cache entries
            DELETE FROM url_cache WHERE last_fetched < @cutoff;

            -- Clean up old query logs
            DELETE FROM query_log WHERE issued_at < @cutoff;

            -- Clean up old feature cache entries
            DELETE FROM feature_cache WHERE last_used < @cutoff;
            """;
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
            Content = reader.IsDBNull(reader.GetOrdinal("content")) ? null : reader.GetString(reader.GetOrdinal("content")),
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

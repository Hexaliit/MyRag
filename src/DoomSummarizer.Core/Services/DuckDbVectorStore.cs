using DuckDB.NET.Data;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// DuckDB-backed vector store with HNSW indexing for fast similarity search.
/// Handles knowledge graph (entities, relationships, mentions) and item embeddings.
/// Single-file database (~/.doomsummarizer/vectors.duckdb).
/// </summary>
public class DuckDbVectorStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly int _dim;
    private DuckDBConnection? _conn;

    /// <summary>
    /// Create a new DuckDB vector store.
    /// </summary>
    /// <param name="dbPath">Path to the .duckdb file</param>
    /// <param name="embeddingDimension">Embedding vector dimension (384 for all-MiniLM-L6-v2)</param>
    public DuckDbVectorStore(string dbPath, int embeddingDimension = 384)
    {
        _dbPath = dbPath;
        _dim = embeddingDimension;
    }

    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new DuckDBConnection($"Data Source={_dbPath}");
        await _conn.OpenAsync();

        // Install and load VSS extension with persistent HNSW
        await ExecAsync("INSTALL vss; LOAD vss; SET hnsw_enable_experimental_persistence = true;");

        await CreateTablesAsync();
    }

    private async Task CreateTablesAsync()
    {
        // Item embeddings - for finding similar articles
        await ExecAsync($"""
            CREATE TABLE IF NOT EXISTS item_embeddings (
                item_id VARCHAR PRIMARY KEY,
                title VARCHAR NOT NULL,
                source VARCHAR,
                url VARCHAR,
                embedding FLOAT[{_dim}],
                entity_profile FLOAT[{_dim}],
                indexed_at TIMESTAMP DEFAULT current_timestamp
            )
            """);

        // Migration: add entity_profile column if not exists
        try
        {
            await ExecAsync($"ALTER TABLE item_embeddings ADD COLUMN entity_profile FLOAT[{_dim}]");
        }
        catch
        {
            // Column already exists
        }

        // Entity nodes with optional embeddings
        await ExecAsync($"""
            CREATE TABLE IF NOT EXISTS entities (
                id VARCHAR PRIMARY KEY,
                name VARCHAR NOT NULL,
                type VARCHAR NOT NULL,
                description VARCHAR,
                first_seen TIMESTAMP NOT NULL DEFAULT current_timestamp,
                last_seen TIMESTAMP NOT NULL DEFAULT current_timestamp,
                mention_count INTEGER DEFAULT 1,
                embedding FLOAT[{_dim}]
            )
            """);

        // Entity-to-item provenance
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS entity_mentions (
                entity_id VARCHAR NOT NULL,
                item_id VARCHAR NOT NULL,
                confidence DOUBLE DEFAULT 0.5,
                context VARCHAR,
                mentioned_at TIMESTAMP NOT NULL DEFAULT current_timestamp,
                PRIMARY KEY (entity_id, item_id)
            )
            """);

        // Entity co-occurrence relationships
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS entity_relationships (
                source_entity_id VARCHAR NOT NULL,
                target_entity_id VARCHAR NOT NULL,
                relationship_type VARCHAR DEFAULT 'co_occurs',
                weight DOUBLE DEFAULT 1.0,
                first_seen TIMESTAMP NOT NULL DEFAULT current_timestamp,
                last_seen TIMESTAMP NOT NULL DEFAULT current_timestamp,
                PRIMARY KEY (source_entity_id, target_entity_id)
            )
            """);

        // HNSW indexes for fast cosine similarity search
        try
        {
            await ExecAsync($"""
                CREATE INDEX IF NOT EXISTS item_emb_hnsw
                ON item_embeddings USING HNSW (embedding)
                WITH (metric = 'cosine')
                """);
        }
        catch
        {
            // Index may fail if table is empty or VSS not available - non-fatal
        }

        try
        {
            await ExecAsync($"""
                CREATE INDEX IF NOT EXISTS entity_emb_hnsw
                ON entities USING HNSW (embedding)
                WITH (metric = 'cosine')
                """);
        }
        catch
        {
            // Non-fatal
        }

        // HNSW index for entity profiles (TF×IDF×confidence weighted entity embeddings)
        try
        {
            await ExecAsync($"""
                CREATE INDEX IF NOT EXISTS item_entity_profile_hnsw
                ON item_embeddings USING HNSW (entity_profile)
                WITH (metric = 'cosine')
                """);
        }
        catch
        {
            // Non-fatal
        }
    }

    // --- Item Embeddings ---

    /// <summary>
    /// Upsert an item embedding for HNSW-backed similarity search.
    /// </summary>
    public async Task UpsertItemEmbeddingAsync(string itemId, string title, string? source, string? url, float[] embedding)
    {
        await ExecAsync(
            """
            INSERT INTO item_embeddings (item_id, title, source, url, embedding, indexed_at)
            VALUES ($1, $2, $3, $4, $5, now())
            ON CONFLICT (item_id) DO UPDATE SET
                embedding = $5,
                indexed_at = now()
            """,
            itemId, title, source, url, embedding);
    }

    /// <summary>
    /// Find similar items using HNSW cosine similarity search.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarItemsAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f)
    {
        var results = new List<(string, string, string?, float)>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = $"""
            SELECT item_id, title, url,
                   1.0 - array_cosine_distance(embedding, $1::FLOAT[{_dim}]) as similarity
            FROM item_embeddings
            WHERE embedding IS NOT NULL
            ORDER BY array_cosine_distance(embedding, $1::FLOAT[{_dim}])
            LIMIT $2
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = queryEmbedding });
        cmd.Parameters.Add(new DuckDBParameter { Value = topK });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var similarity = reader.GetFloat(3);
            if (similarity >= minSimilarity)
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    similarity));
            }
        }

        return results;
    }

    // --- Entity Profile Operations ---

    /// <summary>
    /// Upsert an item's entity profile for HNSW-backed entity similarity search.
    /// Entity profiles are TF×IDF×confidence weighted embeddings that encode the document's entity fingerprint.
    /// </summary>
    public async Task UpsertItemEntityProfileAsync(string itemId, float[] entityProfile)
    {
        await ExecAsync(
            """
            UPDATE item_embeddings
            SET entity_profile = $2
            WHERE item_id = $1
            """,
            itemId, entityProfile);
    }

    /// <summary>
    /// Find documents with similar entity profiles using HNSW cosine similarity search.
    /// Returns item IDs ordered by entity profile similarity.
    /// </summary>
    public async Task<List<(string itemId, string title, float similarity)>> FindRelatedByEntityProfileAsync(
        float[] queryEntityProfile, int topK = 5, float minSimilarity = 0.3f)
    {
        var results = new List<(string, string, float)>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = $"""
            SELECT item_id, title,
                   1.0 - array_cosine_distance(entity_profile, $1::FLOAT[{_dim}]) as similarity
            FROM item_embeddings
            WHERE entity_profile IS NOT NULL
            ORDER BY array_cosine_distance(entity_profile, $1::FLOAT[{_dim}])
            LIMIT $2
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = queryEntityProfile });
        cmd.Parameters.Add(new DuckDBParameter { Value = topK });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var similarity = reader.GetFloat(2);
            if (similarity >= minSimilarity)
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    similarity));
            }
        }

        return results;
    }

    /// <summary>
    /// Check if any items have entity profiles computed.
    /// Used to determine whether to enable entity profile enrichment.
    /// </summary>
    public async Task<bool> HasEntityProfilesAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM item_embeddings WHERE entity_profile IS NOT NULL LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync();
    }

    /// <summary>
    /// Get entity profiles for a list of item IDs.
    /// Used to compute aggregate entity profiles for query expansion.
    /// </summary>
    public async Task<Dictionary<string, float[]>> GetEntityProfilesAsync(IEnumerable<string> itemIds)
    {
        var profiles = new Dictionary<string, float[]>();
        var idList = itemIds.ToList();
        if (idList.Count == 0) return profiles;

        using var cmd = _conn!.CreateCommand();

        // Build placeholders
        var placeholders = string.Join(", ", idList.Select((_, i) => $"${i + 1}"));
        // Use array_to_string to convert FLOAT[] to comma-separated string for reliable reading
        cmd.CommandText = $"""
            SELECT item_id, array_to_string(entity_profile, ',')
            FROM item_embeddings
            WHERE item_id IN ({placeholders}) AND entity_profile IS NOT NULL
            """;

        foreach (var id in idList)
        {
            cmd.Parameters.Add(new DuckDBParameter { Value = id });
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var itemId = reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                var profileStr = reader.GetString(1);
                var profile = ParseFloatArray(profileStr);
                if (profile.Length > 0)
                    profiles[itemId] = profile;
            }
        }

        return profiles;
    }

    /// <summary>
    /// Parse a comma-separated float string into a float array.
    /// </summary>
    private static float[] ParseFloatArray(string str)
    {
        if (string.IsNullOrEmpty(str)) return [];
        var parts = str.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var val))
                result[i] = val;
        }
        return result;
    }

    /// <summary>
    /// Get document counts per entity for IDF computation.
    /// Returns a dictionary mapping entity_id → number of documents mentioning that entity.
    /// </summary>
    public async Task<Dictionary<string, int>> GetEntityDocCountsAsync()
    {
        var counts = new Dictionary<string, int>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = """
            SELECT entity_id, COUNT(DISTINCT item_id) as doc_count
            FROM entity_mentions
            GROUP BY entity_id
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <summary>
    /// Get total number of unique documents that have entity mentions.
    /// Used as N in IDF computation.
    /// </summary>
    public async Task<int> GetTotalDocsWithEntitiesAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT item_id) FROM entity_mentions";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    /// <summary>
    /// Get entities for a specific item with their mention counts.
    /// Returns (entityId, name, confidence, mentions) tuples for entity profile computation.
    /// </summary>
    public async Task<List<(string entityId, string name, float confidence, int mentions)>> GetEntitiesForItemAsync(
        string itemId)
    {
        var entities = new List<(string, string, float, int)>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = """
            SELECT e.id, e.name, em.confidence,
                   (SELECT COUNT(*) FROM entity_mentions WHERE entity_id = e.id AND item_id = $1) as mentions
            FROM entity_mentions em
            JOIN entities e ON em.entity_id = e.id
            WHERE em.item_id = $1
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = itemId });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entities.Add((
                reader.GetString(0),
                reader.GetString(1),
                (float)reader.GetDouble(2),
                reader.GetInt32(3)));
        }

        return entities;
    }

    /// <summary>
    /// Update entity embedding for a specific entity.
    /// Used to cache entity text embeddings for future profile computations.
    /// </summary>
    public async Task UpdateEntityEmbeddingAsync(string entityId, float[] embedding)
    {
        // Convert embedding to string for storage
        var embeddingStr = string.Join(",", embedding.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await ExecAsync(
            $"""
            UPDATE entities
            SET embedding = string_split($2, ',')::FLOAT[{_dim}]
            WHERE id = $1 AND embedding IS NULL
            """,
            entityId, embeddingStr);
    }

    /// <summary>
    /// Batch update entity embeddings.
    /// </summary>
    public async Task UpdateEntityEmbeddingsBatchAsync(Dictionary<string, float[]> embeddings)
    {
        foreach (var (entityId, embedding) in embeddings)
        {
            await UpdateEntityEmbeddingAsync(entityId, embedding);
        }
    }

    /// <summary>
    /// Get entity embeddings by entity IDs.
    /// Used to avoid re-embedding entity names during profile computation.
    /// </summary>
    public async Task<Dictionary<string, float[]>> GetEntityEmbeddingsAsync(IEnumerable<string> entityIds)
    {
        var embeddings = new Dictionary<string, float[]>();
        var idList = entityIds.ToList();
        if (idList.Count == 0) return embeddings;

        using var cmd = _conn!.CreateCommand();

        var placeholders = string.Join(", ", idList.Select((_, i) => $"${i + 1}"));
        // Use array_to_string to convert FLOAT[] to comma-separated string for reliable reading
        cmd.CommandText = $"""
            SELECT id, array_to_string(embedding, ',')
            FROM entities
            WHERE id IN ({placeholders}) AND embedding IS NOT NULL
            """;

        foreach (var id in idList)
        {
            cmd.Parameters.Add(new DuckDBParameter { Value = id });
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var entityId = reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                var embeddingStr = reader.GetString(1);
                var embedding = ParseFloatArray(embeddingStr);
                if (embedding.Length > 0)
                    embeddings[entityId] = embedding;
            }
        }

        return embeddings;
    }

    /// <summary>
    /// Get all item IDs that have entity mentions but no entity profile computed yet.
    /// Used for backfilling entity profiles on existing KB items.
    /// </summary>
    public async Task<List<string>> GetItemsWithoutEntityProfilesAsync(int limit = 100)
    {
        var itemIds = new List<string>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = """
            SELECT DISTINCT em.item_id
            FROM entity_mentions em
            JOIN item_embeddings ie ON em.item_id = ie.item_id
            WHERE ie.entity_profile IS NULL
            LIMIT $1
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = limit });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            itemIds.Add(reader.GetString(0));
        }

        return itemIds;
    }

    /// <summary>
    /// Get all entities for a batch of items.
    /// Returns (itemId, entityId, name, type, confidence, mentions) tuples.
    /// </summary>
    public async Task<List<(string itemId, string entityId, string name, string type, float confidence, int mentions)>>
        GetEntitiesForItemsAsync(IEnumerable<string> itemIds)
    {
        var results = new List<(string, string, string, string, float, int)>();
        var idList = itemIds.ToList();
        if (idList.Count == 0) return results;

        using var cmd = _conn!.CreateCommand();

        var placeholders = string.Join(", ", idList.Select((_, i) => $"${i + 1}"));
        cmd.CommandText = $"""
            SELECT em.item_id, e.id, e.name, e.type, em.confidence, 1 as mentions
            FROM entity_mentions em
            JOIN entities e ON em.entity_id = e.id
            WHERE em.item_id IN ({placeholders})
            ORDER BY em.item_id
            """;

        foreach (var id in idList)
        {
            cmd.Parameters.Add(new DuckDBParameter { Value = id });
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),  // item_id
                reader.GetString(1),  // entity_id
                reader.GetString(2),  // name
                reader.GetString(3),  // type
                (float)reader.GetDouble(4),  // confidence
                reader.GetInt32(5))); // mentions
        }

        return results;
    }

    // --- Entity Operations ---

    /// <summary>
    /// Upsert an entity, incrementing mention count and updating last_seen.
    /// </summary>
    public async Task UpsertEntityAsync(string id, string name, string type, double confidence, float[]? embedding = null)
    {
        if (embedding != null)
        {
            await ExecAsync(
                """
                INSERT INTO entities (id, name, type, mention_count, embedding)
                VALUES ($1, $2, $3, 1, $4)
                ON CONFLICT (id) DO UPDATE SET
                    last_seen = now(),
                    mention_count = entities.mention_count + 1,
                    embedding = COALESCE($4, entities.embedding)
                """,
                id, name, type, embedding);
        }
        else
        {
            await ExecAsync(
                """
                INSERT INTO entities (id, name, type, mention_count)
                VALUES ($1, $2, $3, 1)
                ON CONFLICT (id) DO UPDATE SET
                    last_seen = now(),
                    mention_count = entities.mention_count + 1
                """,
                id, name, type);
        }
    }

    /// <summary>
    /// Record that an entity was mentioned in an article.
    /// </summary>
    public async Task UpsertEntityMentionAsync(string entityId, string itemId, double confidence, string? context = null)
    {
        await ExecAsync(
            """
            INSERT INTO entity_mentions (entity_id, item_id, confidence, context)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (entity_id, item_id) DO UPDATE SET
                confidence = GREATEST(entity_mentions.confidence, $3)
            """,
            entityId, itemId, confidence, context);
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

        await ExecAsync(
            """
            INSERT INTO entity_relationships (source_entity_id, target_entity_id, relationship_type, weight)
            VALUES ($1, $2, $3, 1.0)
            ON CONFLICT (source_entity_id, target_entity_id) DO UPDATE SET
                weight = entity_relationships.weight + 1.0,
                last_seen = now()
            """,
            s, t, relType);
    }

    /// <summary>
    /// Find entities similar to a query embedding using HNSW.
    /// </summary>
    public async Task<List<(GraphEntity entity, float similarity)>> FindSimilarEntitiesAsync(
        float[] queryEmbedding, int topK = 10)
    {
        var results = new List<(GraphEntity, float)>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = $"""
            SELECT e.id, e.name, e.type, e.mention_count, e.first_seen, e.last_seen,
                   1.0 - array_cosine_distance(e.embedding, $1::FLOAT[{_dim}]) as similarity,
                   (SELECT COUNT(DISTINCT item_id) FROM entity_mentions WHERE entity_id = e.id) as article_count
            FROM entities e
            WHERE e.embedding IS NOT NULL
            ORDER BY array_cosine_distance(e.embedding, $1::FLOAT[{_dim}])
            LIMIT $2
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = queryEmbedding });
        cmd.Parameters.Add(new DuckDBParameter { Value = topK });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var entity = new GraphEntity
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Type = reader.GetString(2),
                MentionCount = reader.GetInt32(3),
                FirstSeen = reader.GetDateTime(4),
                LastSeen = reader.GetDateTime(5),
                ArticleCount = reader.GetInt32(7)
            };
            results.Add((entity, reader.GetFloat(6)));
        }

        return results;
    }

    /// <summary>
    /// Get top entities by mention count with freshness weighting.
    /// </summary>
    public async Task<List<GraphEntity>> GetTopEntitiesAsync(int limit = 20, string? type = null, int? daysBack = null)
    {
        var entities = new List<GraphEntity>();
        using var cmd = _conn!.CreateCommand();

        var conditions = new List<string>();
        var paramIdx = 1;

        if (type != null)
        {
            conditions.Add($"e.type = ${paramIdx}");
            cmd.Parameters.Add(new DuckDBParameter { Value = type });
            paramIdx++;
        }
        if (daysBack != null)
        {
            conditions.Add($"e.last_seen >= current_timestamp - INTERVAL '{daysBack} days'");
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT e.id, e.name, e.type, e.mention_count, e.first_seen, e.last_seen,
                   COUNT(DISTINCT em.item_id) as article_count
            FROM entities e
            LEFT JOIN entity_mentions em ON e.id = em.entity_id
            {whereClause}
            GROUP BY e.id, e.name, e.type, e.mention_count, e.first_seen, e.last_seen
            ORDER BY e.mention_count DESC
            LIMIT ${paramIdx}
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = limit });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entities.Add(new GraphEntity
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Type = reader.GetString(2),
                MentionCount = reader.GetInt32(3),
                FirstSeen = reader.GetDateTime(4),
                LastSeen = reader.GetDateTime(5),
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
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = """
            SELECT r.source_entity_id, r.target_entity_id, r.relationship_type, r.weight,
                   COALESCE(es.name, r.source_entity_id) as source_name,
                   COALESCE(et.name, r.target_entity_id) as target_name
            FROM entity_relationships r
            LEFT JOIN entities es ON r.source_entity_id = es.id
            LEFT JOIN entities et ON r.target_entity_id = et.id
            WHERE r.source_entity_id = $1 OR r.target_entity_id = $1
            ORDER BY r.weight DESC
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = entityId });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relationships.Add(new GraphRelationship
            {
                SourceId = reader.GetString(0),
                TargetId = reader.GetString(1),
                Type = reader.GetString(2),
                Weight = (float)reader.GetDouble(3),
                SourceName = reader.GetString(4),
                TargetName = reader.GetString(5)
            });
        }

        return relationships;
    }

    /// <summary>
    /// Get articles that mention a specific entity.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, double confidence)>> GetArticlesForEntityAsync(
        string entityId)
    {
        var articles = new List<(string, string, string?, double)>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = """
            SELECT ie.item_id, ie.title, ie.url, em.confidence
            FROM entity_mentions em
            JOIN item_embeddings ie ON em.item_id = ie.item_id
            WHERE em.entity_id = $1
            ORDER BY em.confidence DESC
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = entityId });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            articles.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDouble(3)));
        }

        return articles;
    }

    /// <summary>
    /// Get graph statistics.
    /// </summary>
    public async Task<(int entities, int relationships, int mentions, int itemEmbeddings)> GetStatsAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM entities),
                (SELECT COUNT(*) FROM entity_relationships),
                (SELECT COUNT(*) FROM entity_mentions),
                (SELECT COUNT(*) FROM item_embeddings)
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                Convert.ToInt32(reader.GetValue(0)),
                Convert.ToInt32(reader.GetValue(1)),
                Convert.ToInt32(reader.GetValue(2)),
                Convert.ToInt32(reader.GetValue(3)));
        }
        return (0, 0, 0, 0);
    }

    /// <summary>
    /// Delete all vector store data — item embeddings, entities, mentions, relationships.
    /// Used by --clear-storage to reset to a clean state.
    /// </summary>
    public async Task ClearAllAsync()
    {
        await ExecAsync("""
            DELETE FROM entity_mentions;
            DELETE FROM entity_relationships;
            DELETE FROM entities;
            DELETE FROM item_embeddings;
            """);
    }

    /// <summary>
    /// Cleanup old data. Keeps entities but prunes stale mentions and orphans.
    /// </summary>
    public async Task CleanupAsync(int retentionDays)
    {
        await ExecAsync($"""
            DELETE FROM entity_mentions
            WHERE mentioned_at < current_timestamp - INTERVAL '{retentionDays} days';

            DELETE FROM item_embeddings
            WHERE indexed_at < current_timestamp - INTERVAL '{retentionDays} days';

            DELETE FROM entities
            WHERE id NOT IN (SELECT DISTINCT entity_id FROM entity_mentions);

            DELETE FROM entity_relationships
            WHERE source_entity_id NOT IN (SELECT id FROM entities)
               OR target_entity_id NOT IN (SELECT id FROM entities);
            """);
    }

    // --- Helpers ---

    private async Task ExecAsync(string sql, params object?[] parameters)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;

        for (var i = 0; i < parameters.Length; i++)
        {
            cmd.Parameters.Add(new DuckDBParameter { Value = parameters[i] ?? DBNull.Value });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn != null)
        {
            await _conn.CloseAsync();
            await _conn.DisposeAsync();
        }
    }
}

using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Entity graph operations: entities, mentions, relationships, co-occurrence discovery.
///     Tables: entities, entity_mentions, entity_relationships
/// </summary>
public partial class StorageService
{
    // --- Knowledge Graph Methods ---

    /// <summary>
    ///     Upsert an entity, incrementing mention count and updating last_seen.
    /// </summary>
    public async Task UpsertEntityAsync(string id, string name, string type, double confidence,
        float[]? embedding = null)
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
        cmd.Parameters.AddWithValue("@embedding",
            embedding != null ? EmbeddingCompat.ToBytes(embedding) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     Record that an entity was mentioned in a specific article.
    /// </summary>
    public async Task UpsertEntityMentionAsync(string entityId, string itemId, double confidence,
        string? context = null)
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
    ///     Upsert a co-occurrence relationship between two entities.
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
    ///     Get top entities by mention count, optionally filtered by type and freshness.
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

        return entities;
    }

    /// <summary>
    ///     Get relationships for an entity.
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
            relationships.Add(new GraphRelationship
            {
                SourceId = reader.GetString(0),
                TargetId = reader.GetString(1),
                Type = reader.GetString(2),
                Weight = reader.GetFloat(3),
                SourceName = reader.GetString(4),
                TargetName = reader.GetString(5)
            });

        return relationships;
    }

    /// <summary>
    ///     Get articles that mention a specific entity.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, double confidence)>> GetArticlesForEntityAsync(
        string entityId)
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
            articles.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDouble(3)
            ));

        return articles;
    }

    /// <summary>
    ///     Get all entities associated with a set of item IDs.
    ///     Returns a dictionary mapping item_id → list of (entity name, type, confidence).
    ///     Used to enrich theme briefings with real NER entities from the persistent knowledge graph.
    /// </summary>
    public async Task<Dictionary<string, List<(string name, string type, double confidence)>>> GetEntitiesForItemsAsync(
        IEnumerable<string> itemIds)
    {
        var result = new Dictionary<string, List<(string, string, double)>>(StringComparer.OrdinalIgnoreCase);
        var idList = itemIds.ToList();
        if (idList.Count == 0) return result;

        // SQLite doesn't support array parameters; batch with IN clause
        // Process in chunks of 100 to avoid SQL parameter limits
        foreach (var chunk in idList.Chunk(100))
        {
            await using var cmd = _connection!.CreateCommand();
            var placeholders = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var paramName = $"@id{i}";
                placeholders.Add(paramName);
                cmd.Parameters.AddWithValue(paramName, chunk[i]);
            }

            cmd.CommandText = $"""
                               SELECT em.item_id, e.name, e.type, em.confidence
                               FROM entity_mentions em
                               JOIN entities e ON em.entity_id = e.id
                               WHERE em.item_id IN ({string.Join(", ", placeholders)})
                               ORDER BY em.confidence DESC
                               """;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var itemId = reader.GetString(0);
                var name = reader.GetString(1);
                var entityType = reader.GetString(2);
                var confidence = reader.GetDouble(3);

                if (!result.TryGetValue(itemId, out var list))
                {
                    list = [];
                    result[itemId] = list;
                }

                list.Add((name, entityType, confidence));
            }
        }

        return result;
    }

    /// <summary>
    ///     Get graph statistics.
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
        if (await reader.ReadAsync()) return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        return (0, 0, 0);
    }

    /// <summary>
    ///     Find documents that share entities with the given item IDs.
    ///     Returns item IDs of related documents, ordered by shared entity count.
    /// </summary>
    public async Task<List<string>> FindRelatedByEntitiesAsync(
        List<string> itemIds, List<string>? excludeIds = null, int limit = 3)
    {
        if (itemIds.Count == 0) return [];
        excludeIds ??= itemIds;

        var ids = new List<string>();
        await using var cmd = _connection!.CreateCommand();

        // Build parameter placeholders for source item IDs
        var srcPlaceholders = new List<string>();
        for (var i = 0; i < itemIds.Count; i++)
        {
            srcPlaceholders.Add($"@src{i}");
            cmd.Parameters.AddWithValue($"@src{i}", itemIds[i]);
        }

        // Build parameter placeholders for excluded item IDs
        var exclPlaceholders = new List<string>();
        for (var i = 0; i < excludeIds.Count; i++)
        {
            exclPlaceholders.Add($"@excl{i}");
            cmd.Parameters.AddWithValue($"@excl{i}", excludeIds[i]);
        }

        cmd.CommandText = $"""
                           SELECT em2.item_id, COUNT(DISTINCT em2.entity_id) as shared_count,
                                  SUM(em2.confidence) as total_confidence
                           FROM entity_mentions em1
                           JOIN entity_mentions em2 ON em1.entity_id = em2.entity_id
                           WHERE em1.item_id IN ({string.Join(", ", srcPlaceholders)})
                             AND em2.item_id NOT IN ({string.Join(", ", exclPlaceholders)})
                           GROUP BY em2.item_id
                           HAVING shared_count >= 2
                           ORDER BY shared_count DESC, total_confidence DESC
                           LIMIT @limit
                           """;
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));

        return ids;
    }
}
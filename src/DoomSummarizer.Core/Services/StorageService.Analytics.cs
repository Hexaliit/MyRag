using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;

namespace DoomSummarizer.Services;

/// <summary>
///     Trend analysis, summary storage, collection management, and maintenance operations.
///     Tables: daily_stats, summaries, items (aggregate queries)
/// </summary>
public partial class StorageService
{
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
        var totalItems = 0;
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

    /// <summary>
    ///     List all KB collections (distinct source prefixes) with item counts and stats.
    /// </summary>
    public async Task<List<CollectionInfo>> GetCollectionsAsync()
    {
        var collections = new List<CollectionInfo>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          SELECT source,
                                 COUNT(*) as item_count,
                                 COUNT(CASE WHEN embedding IS NOT NULL THEN 1 END) as with_embeddings,
                                 MIN(fetched_at) as earliest,
                                 MAX(fetched_at) as latest,
                                 AVG(CASE WHEN content IS NOT NULL THEN LENGTH(content) ELSE 0 END) as avg_content_len
                          FROM items
                          GROUP BY source
                          ORDER BY MAX(fetched_at) DESC
                          """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            collections.Add(new CollectionInfo
            {
                Source = reader.GetString(0),
                ItemCount = reader.GetInt32(1),
                WithEmbeddings = reader.GetInt32(2),
                Earliest = DateTimeOffset.Parse(reader.GetString(3)),
                Latest = DateTimeOffset.Parse(reader.GetString(4)),
                AvgContentLength = reader.IsDBNull(5) ? 0 : (int)reader.GetDouble(5)
            });

        return collections;
    }

    /// <summary>
    ///     Get all items for a given source (collection).
    /// </summary>
    public async Task<List<StoredItem>> GetItemsBySourceAsync(string source, int limit = 500)
    {
        var items = new List<StoredItem>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE source = @source ORDER BY fetched_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) items.Add(ReadStoredItem(reader));

        return items;
    }

    /// <summary>
    ///     Delete all stored data — items, entities, queries, caches.
    ///     Used by --clear-storage to reset to a clean state.
    /// </summary>
    public async Task ClearAllAsync()
    {
        // Core storage tables (always exist after InitializeAsync)
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          DELETE FROM entity_mentions;
                          DELETE FROM entity_relationships;
                          DELETE FROM entities;
                          DELETE FROM items;
                          DELETE FROM summaries;
                          DELETE FROM query_log;
                          DELETE FROM url_cache;
                          DELETE FROM feature_cache;
                          DELETE FROM items_fts;
                          DELETE FROM keyword_corpus;
                          """;
        await cmd.ExecuteNonQueryAsync();

        // Budget/circuit tables (may not exist yet — created by other services)
        foreach (var table in new[] { "api_usage", "api_usage_total", "circuit_state" })
            try
            {
                await using var extra = _connection.CreateCommand();
                extra.CommandText = $"DELETE FROM {table}";
                await extra.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Table doesn't exist yet — that's fine
            }
    }

    /// <summary>
    ///     Delete all items (and related FTS/entity data) for a given source tag.
    ///     Used by ManualLoader to clear the manual corpus before re-indexing.
    /// </summary>
    public async Task DeleteItemsBySourceAsync(string source)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          -- Delete FTS entries for items in this source
                          DELETE FROM items_fts WHERE item_id IN (SELECT id FROM items WHERE source = @source);

                          -- Delete entity mentions for items in this source
                          DELETE FROM entity_mentions WHERE item_id IN (SELECT id FROM items WHERE source = @source);

                          -- Delete the items themselves
                          DELETE FROM items WHERE source = @source;

                          -- Clean up entities with no remaining mentions
                          DELETE FROM entities WHERE id NOT IN (SELECT DISTINCT entity_id FROM entity_mentions);

                          -- Clean up orphaned relationships
                          DELETE FROM entity_relationships
                          WHERE source_entity_id NOT IN (SELECT id FROM entities)
                             OR target_entity_id NOT IN (SELECT id FROM entities);
                          """;
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync();
    }

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

                          -- Clean up orphaned FTS entries (items deleted above)
                          DELETE FROM items_fts WHERE item_id NOT IN (SELECT id FROM items);

                          -- Clean up keyword corpus for terms with zero documents
                          DELETE FROM keyword_corpus WHERE document_count <= 0;
                          """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync();
    }
}
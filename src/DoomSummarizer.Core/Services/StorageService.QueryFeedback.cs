using System.Text.Json;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Query feedback and LFU (Least Frequently Used) tracking.
/// Tables: query_log, item_usage
/// </summary>
public partial class StorageService
{
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
        cmd.Parameters.AddWithValue("@embedding", queryEmbedding != null ? EmbeddingCompat.ToBytes(queryEmbedding) : DBNull.Value);
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
            var storedEmbedding = EmbeddingCompat.FromBytes((byte[])reader["query_embedding"]);
            var similarity = VectorMath.CosineSimilarity(queryEmbedding, storedEmbedding);

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
}

using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Item retrieval operations: recent items, similarity search, batch loading by ID/source.
/// Table: items (read operations)
/// </summary>
public partial class StorageService
{
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

    public async Task<List<StoredItem>> FindSimilarAsync(float[] embedding, int limit = 10, double threshold = 0.85, string? source = null)
    {
        // Simple brute-force similarity search - works fine for small datasets
        var items = new List<(StoredItem item, float similarity)>();

        await using var cmd = _connection!.CreateCommand();
        if (source != null)
        {
            cmd.CommandText = "SELECT * FROM items WHERE embedding IS NOT NULL AND source = @source ORDER BY fetched_at DESC LIMIT 5000";
            cmd.Parameters.AddWithValue("@source", source);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM items WHERE embedding IS NOT NULL ORDER BY fetched_at DESC LIMIT 1000";
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = ReadStoredItem(reader);
            if (item.Embedding == null) continue;

            var storedEmbedding = EmbeddingCompat.FromBytes(item.Embedding);
            var similarity = VectorMath.CosineSimilarity(embedding, storedEmbedding);

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
}

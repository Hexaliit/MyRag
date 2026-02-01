using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Item retrieval operations: recent items, similarity search, batch loading by ID/source.
/// Table: items (read operations)
/// </summary>
public partial class StorageService
{
    public async Task<List<StoredItem>> GetRecentItemsAsync(int days = 7, string? source = null)
        => await GetRecentItemsAsync(days, source != null ? [source] : null);

    public async Task<List<StoredItem>> GetRecentItemsAsync(int days, IReadOnlyList<string>? sources)
    {
        var items = new List<StoredItem>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        var filterSet = SourceFilterSet.Parse(sources);

        await using var cmd = _connection!.CreateCommand();
        var clauses = new List<string> { "fetched_at >= @cutoff" };
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        AppendFilterClauses(filterSet, clauses, cmd);

        cmd.CommandText = $"SELECT * FROM items WHERE {string.Join(" AND ", clauses)} ORDER BY fetched_at DESC";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(ReadStoredItem(reader));
        }

        return items;
    }

    public async Task<List<StoredItem>> FindSimilarAsync(float[] embedding, int limit = 10, double threshold = 0.85, string? source = null)
        => await FindSimilarAsync(embedding, limit, threshold, source != null ? [source] : null);

    public async Task<List<StoredItem>> FindSimilarAsync(float[] embedding, int limit, double threshold, IReadOnlyList<string>? sources)
    {
        // Simple brute-force similarity search - works fine for small datasets
        var items = new List<(StoredItem item, float similarity)>();
        var filterSet = SourceFilterSet.Parse(sources);

        await using var cmd = _connection!.CreateCommand();
        var clauses = new List<string> { "embedding IS NOT NULL" };

        AppendFilterClauses(filterSet, clauses, cmd);

        var dbLimit = filterSet.HasFilters ? 5000 : 1000;
        cmd.CommandText = $"SELECT * FROM items WHERE {string.Join(" AND ", clauses)} ORDER BY fetched_at DESC LIMIT {dbLimit}";

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

    // --- URL cache methods ---

    /// <summary>
    /// Find a stored item by its URL. Returns null if no item with this URL exists.
    /// Used for URL-level deduplication: if a web search returns a URL that's already in storage
    /// (e.g., from a previous crawl), we can reuse the cached content.
    /// </summary>
    public async Task<StoredItem?> FindByUrlAsync(string url)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE url = @url LIMIT 1";
        cmd.Parameters.AddWithValue("@url", url);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadStoredItem(reader) : null;
    }

    /// <summary>
    /// Mark an item as web-validated: a web search found this URL, confirming it's still relevant.
    /// KB items with a recent web_validated_at are included in general queries despite being KB sources.
    /// </summary>
    public async Task WebValidateByUrlAsync(string url)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE items SET web_validated_at = @now WHERE url = @url";
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@url", url);
        await cmd.ExecuteNonQueryAsync();
    }

    // --- Shared filter clause builder ---

    /// <summary>
    /// Append SQL WHERE clauses for a SourceFilterSet, handling include, exclude,
    /// ExcludeKbSources (with web-validation override), and URL glob exclusions.
    /// Self-manages its own @wv_cutoff parameter for web-validation (30-day window).
    /// </summary>
    private static void AppendFilterClauses(
        SourceFilterSet filterSet,
        List<string> clauses,
        Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        if (filterSet.ExcludeKbSources)
        {
            // Web-validation window: KB items validated by a web search within this
            // period are considered "still relevant" and included despite being KB sources.
            var wvCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
            cmd.Parameters.AddWithValue("@wv_cutoff", wvCutoff);

            if (filterSet.Include.Count > 0)
            {
                // Combo mode (web + specific KB): items matching explicit includes
                // OR non-KB items OR web-validated KB items
                var includeList = string.Join(", ",
                    filterSet.Include.Select((_, i) => $"@si{i}"));
                clauses.Add($"""
                    (source IN ({includeList})
                     OR (source NOT LIKE 'crawl:%' AND source NOT LIKE 'file:%'
                         AND source NOT LIKE 'youtube:%' AND source != 'manual')
                     OR web_validated_at >= @wv_cutoff)
                    """);
                for (var i = 0; i < filterSet.Include.Count; i++)
                    cmd.Parameters.AddWithValue($"@si{i}", filterSet.Include[i]);
            }
            else
            {
                // Pure web mode: non-KB sources + web-validated KB items
                clauses.Add("""
                    ((source NOT LIKE 'crawl:%' AND source NOT LIKE 'file:%'
                      AND source NOT LIKE 'youtube:%' AND source != 'manual')
                     OR web_validated_at >= @wv_cutoff)
                    """);
            }
        }
        else
        {
            // Standard include filter (no KB exclusion)
            if (filterSet.Include.Count > 0)
            {
                var placeholders = string.Join(", ", filterSet.Include.Select((_, i) => $"@si{i}"));
                clauses.Add($"source IN ({placeholders})");
                for (var i = 0; i < filterSet.Include.Count; i++)
                    cmd.Parameters.AddWithValue($"@si{i}", filterSet.Include[i]);
            }
        }

        // Standard source tag exclusions
        for (var i = 0; i < filterSet.Exclude.Count; i++)
        {
            clauses.Add($"source != @se{i}");
            cmd.Parameters.AddWithValue($"@se{i}", filterSet.Exclude[i]);
        }

        // URL glob exclusions
        for (var g = 0; g < filterSet.UrlExcludes.Count; g++)
        {
            clauses.Add($"(url IS NULL OR url NOT LIKE @uglob{g} ESCAPE '\\')");
            cmd.Parameters.AddWithValue($"@uglob{g}",
                SourceFilterSet.GlobToSqlLike(filterSet.UrlExcludes[g]));
        }
    }
}

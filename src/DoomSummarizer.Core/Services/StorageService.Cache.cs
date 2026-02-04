namespace DoomSummarizer.Services;

/// <summary>
///     Feature cache (entity disambiguation) and URL cache (ETag/content hash).
///     Tables: feature_cache, url_cache
/// </summary>
public partial class StorageService
{
    // --- Feature Cache Methods (Entity Disambiguation) ---

    /// <summary>
    ///     Get a cached feature embedding by term. Returns (embeddingBytes, category) or null.
    ///     Bumps hit_count and last_used on cache hit.
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
    ///     Insert or update a feature cache entry.
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
    ///     Get cached info for a URL (ETag, content hash, last fetch time).
    /// </summary>
    public async Task<UrlCacheEntry?> GetUrlCacheAsync(string url)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText =
            "SELECT url, content_hash, etag, last_modified, last_fetched, content_length, hit_count FROM url_cache WHERE url = @url";
        cmd.Parameters.AddWithValue("@url", NormalizeCacheUrl(url));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
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
        return null;
    }

    /// <summary>
    ///     Check if a URL was fetched recently and content hasn't changed.
    ///     Returns true if we can skip processing.
    /// </summary>
    public async Task<bool> IsUrlFreshAsync(string url, int decayHours = 4)
    {
        var entry = await GetUrlCacheAsync(url);
        if (entry == null) return false;
        return (DateTimeOffset.UtcNow - entry.LastFetched).TotalHours < decayHours;
    }

    /// <summary>
    ///     Update the URL cache after a fetch.
    /// </summary>
    public async Task UpdateUrlCacheAsync(string url, string? contentHash, string? etag, string? lastModified,
        int contentLength)
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
    ///     Check if content hash matches what's cached (content unchanged).
    /// </summary>
    public async Task<bool> IsContentUnchangedAsync(string url, string contentHash)
    {
        var entry = await GetUrlCacheAsync(url);
        return entry?.ContentHash == contentHash;
    }

    /// <summary>
    ///     Get all URL cache entries (for pre-loading ETag/Last-Modified lookups before a crawl).
    /// </summary>
    public async Task<List<UrlCacheEntry>> GetAllUrlCacheEntriesAsync()
    {
        var entries = new List<UrlCacheEntry>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText =
            "SELECT url, content_hash, etag, last_modified, last_fetched, content_length, hit_count FROM url_cache";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(new UrlCacheEntry
            {
                Url = reader.GetString(0),
                ContentHash = reader.IsDBNull(1) ? null : reader.GetString(1),
                ETag = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastModified = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastFetched = DateTimeOffset.Parse(reader.GetString(4)),
                ContentLength = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                HitCount = reader.GetInt32(6)
            });
        return entries;
    }

    private static string NormalizeCacheUrl(string url)
    {
        return url.Split('?')[0].Split('#')[0].TrimEnd('/').ToLowerInvariant();
    }
}
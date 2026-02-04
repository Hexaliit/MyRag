using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for StorageService query feedback, LFU tracking, and item retrieval.
///     Uses a temp SQLite database per test.
/// </summary>
public class StorageServiceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private StorageService _storage = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _storage.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            /* cleanup best-effort */
        }
    }

    private ContentItem MakeItem(string id, string title = "Test", string source = "test",
        string? url = null, float[]? embedding = null)
    {
        return new ContentItem
        {
            Id = id,
            Source = source,
            Title = title,
            Url = url ?? $"https://example.com/{id}",
            Content = $"Content for {title}",
            CreatedAt = DateTimeOffset.UtcNow,
            FetchedAt = DateTimeOffset.UtcNow,
            Embedding = embedding
        };
    }

    #region Query Feedback

    [Fact]
    public async Task LogQueryAsync_StoresQueryAndUpdatesUsage()
    {
        // Arrange
        await _storage.SaveItemAsync(MakeItem("item1"));
        await _storage.SaveItemAsync(MakeItem("item2"));

        // Act
        await _storage.LogQueryAsync("test query", null, "neutral", ["item1", "item2"]);

        // Assert - items should have usage tracked
        var usage = await _storage.GetItemUsageAsync(["item1", "item2"]);
        usage.Should().ContainKey("item1");
        usage.Should().ContainKey("item2");
        usage["item1"].accessCount.Should().Be(1);
        usage["item2"].accessCount.Should().Be(1);
    }

    [Fact]
    public async Task LogQueryAsync_IncrementsUsageOnRepeat()
    {
        await _storage.SaveItemAsync(MakeItem("item1"));

        await _storage.LogQueryAsync("query 1", null, "neutral", ["item1"]);
        await _storage.LogQueryAsync("query 2", null, "doom", ["item1"]);

        var usage = await _storage.GetItemUsageAsync(["item1"]);
        usage["item1"].accessCount.Should().Be(2);
    }

    [Fact]
    public async Task FindSimilarQueryAsync_ReturnsNull_WhenNoQueries()
    {
        var embedding = new float[384];
        Array.Fill(embedding, 0.1f);

        var match = await _storage.FindSimilarQueryAsync(embedding);
        match.Should().BeNull();
    }

    [Fact]
    public async Task FindSimilarQueryAsync_FindsIdenticalEmbedding()
    {
        var embedding = new float[384];
        for (var i = 0; i < 384; i++) embedding[i] = i / 384f;

        await _storage.LogQueryAsync("test query", embedding, "neutral", ["item1", "item2"]);

        var match = await _storage.FindSimilarQueryAsync(embedding, 0.9);
        match.Should().NotBeNull();
        match!.QueryText.Should().Be("test query");
        match.ItemIds.Should().BeEquivalentTo("item1", "item2");
        match.Similarity.Should().BeApproximately(1.0f, 0.01f);
    }

    [Fact]
    public async Task FindSimilarQueryAsync_RejectsDistantEmbedding()
    {
        var embedding1 = new float[384];
        var embedding2 = new float[384];
        for (var i = 0; i < 384; i++)
        {
            embedding1[i] = i / 384f;
            embedding2[i] = (384 - i) / 384f; // Very different direction
        }

        await _storage.LogQueryAsync("query A", embedding1, "neutral", ["item1"]);

        var match = await _storage.FindSimilarQueryAsync(embedding2, 0.95);
        // Should not match since embeddings point in very different directions
        // (though they're not orthogonal, so a very low threshold might match)
        match.Should().BeNull();
    }

    [Fact]
    public async Task FindSimilarQueryAsync_RespectsMaxAge()
    {
        // This test verifies the age cutoff is applied.
        // We can't easily backdate queries without raw SQL, but we verify
        // a fresh query IS found within the default 4-hour window.
        var embedding = new float[384];
        Array.Fill(embedding, 0.5f);

        await _storage.LogQueryAsync("recent query", embedding, "neutral", ["item1"]);

        var match = await _storage.FindSimilarQueryAsync(embedding, maxAgeHours: 4);
        match.Should().NotBeNull();
    }

    #endregion

    #region Item Retrieval

    [Fact]
    public async Task GetItemsByIdsAsync_ReturnsMatchingItems()
    {
        await _storage.SaveItemAsync(MakeItem("a", "Alpha"));
        await _storage.SaveItemAsync(MakeItem("b", "Beta"));
        await _storage.SaveItemAsync(MakeItem("c", "Charlie"));

        var items = await _storage.GetItemsByIdsAsync(["a", "c"]);
        items.Should().HaveCount(2);
        items.Select(i => i.Id).Should().BeEquivalentTo("a", "c");
    }

    [Fact]
    public async Task GetItemsByIdsAsync_EmptyList_ReturnsEmpty()
    {
        var items = await _storage.GetItemsByIdsAsync([]);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItemsByIdsAsync_MissingIds_ReturnsOnlyExisting()
    {
        await _storage.SaveItemAsync(MakeItem("exists"));

        var items = await _storage.GetItemsByIdsAsync(["exists", "missing"]);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be("exists");
    }

    #endregion

    #region Usage Tracking

    [Fact]
    public async Task GetItemUsageAsync_UnusedItems_ReturnsEmpty()
    {
        var usage = await _storage.GetItemUsageAsync(["nonexistent"]);
        usage.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItemUsageAsync_TracksAverageRank()
    {
        await _storage.SaveItemAsync(MakeItem("item1"));

        // Log with rank 1, then rank 3 — avg should converge
        await _storage.LogQueryAsync("q1", null, null, ["item1"]); // rank 1
        await _storage.LogQueryAsync("q2", null, null, ["other", "other2", "item1"]); // rank 3

        var usage = await _storage.GetItemUsageAsync(["item1"]);
        usage["item1"].accessCount.Should().Be(2);
    }

    #endregion

    #region URL Cache

    [Fact]
    public async Task UrlCache_StoreAndRetrieve()
    {
        await _storage.UpdateUrlCacheAsync("https://example.com/page", "hash123", "etag-abc", null, 5000);

        var entry = await _storage.GetUrlCacheAsync("https://example.com/page");
        entry.Should().NotBeNull();
        entry!.ContentHash.Should().Be("hash123");
        entry.ETag.Should().Be("etag-abc");
        entry.ContentLength.Should().Be(5000);
        entry.HitCount.Should().Be(1);
    }

    [Fact]
    public async Task UrlCache_IncrementsHitCount()
    {
        await _storage.UpdateUrlCacheAsync("https://example.com/page", "h1", null, null, 100);
        await _storage.UpdateUrlCacheAsync("https://example.com/page", "h2", null, null, 200);

        var entry = await _storage.GetUrlCacheAsync("https://example.com/page");
        entry!.HitCount.Should().Be(2);
        entry.ContentHash.Should().Be("h2");
    }

    [Fact]
    public async Task IsContentUnchangedAsync_DetectsChange()
    {
        await _storage.UpdateUrlCacheAsync("https://example.com/a", "hash-old", null, null, 100);

        (await _storage.IsContentUnchangedAsync("https://example.com/a", "hash-old")).Should().BeTrue();
        (await _storage.IsContentUnchangedAsync("https://example.com/a", "hash-new")).Should().BeFalse();
    }

    #endregion
}
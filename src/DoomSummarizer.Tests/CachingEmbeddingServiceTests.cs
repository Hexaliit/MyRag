using DoomSummarizer.Tests.Benchmarks;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests;

public class CachingEmbeddingServiceTests
{
    [Fact]
    public async Task EmbedAsync_CachesResult_SecondCallSkipsInner()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        var result1 = await cache.EmbedAsync("hello world");
        var result2 = await cache.EmbedAsync("hello world");

        Assert.Equal(1, inner.SingleEmbedCalls);
        Assert.Equal(result1, result2);

        var stats = cache.GetStats();
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(1, stats.CurrentSize);
    }

    [Fact]
    public async Task EmbedAsync_DifferentTexts_BothComputed()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        await cache.EmbedAsync("alpha");
        await cache.EmbedAsync("beta");

        Assert.Equal(2, inner.SingleEmbedCalls);

        var stats = cache.GetStats();
        Assert.Equal(0, stats.Hits);
        Assert.Equal(2, stats.Misses);
        Assert.Equal(2, stats.CurrentSize);
    }

    [Fact]
    public async Task EmbedBatchAsync_CachesPerItem()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        // First call embeds all 3
        var batch1 = await cache.EmbedBatchAsync(new[] { "a", "b", "c" });
        Assert.Equal(3, batch1.Length);
        Assert.Equal(1, inner.BatchEmbedCalls);
        Assert.Equal(3, inner.TotalTextsEmbedded);

        // Second call with overlapping items — only "d" should be computed
        inner.Reset();
        var batch2 = await cache.EmbedBatchAsync(new[] { "a", "d", "c" });
        Assert.Equal(3, batch2.Length);
        Assert.Equal(1, inner.BatchEmbedCalls);
        Assert.Equal(1, inner.TotalTextsEmbedded); // Only "d" was uncached

        // "a" and "c" should return same values as before
        Assert.Equal(batch1[0], batch2[0]); // "a"
        Assert.Equal(batch1[2], batch2[2]); // "c"
    }

    [Fact]
    public async Task EmbedBatchAsync_AllCached_SkipsInnerCall()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        await cache.EmbedBatchAsync(new[] { "x", "y" });
        inner.Reset();

        // All cached — should not call inner at all
        var batch2 = await cache.EmbedBatchAsync(new[] { "x", "y" });
        Assert.Equal(2, batch2.Length);
        Assert.Equal(0, inner.BatchEmbedCalls);
        Assert.Equal(0, inner.TotalTextsEmbedded);
    }

    [Fact]
    public async Task Eviction_TriggersWhenOverCapacity()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 3);

        // Fill cache to capacity
        await cache.EmbedAsync("one");
        await cache.EmbedAsync("two");
        await cache.EmbedAsync("three");

        var stats1 = cache.GetStats();
        Assert.Equal(3, stats1.CurrentSize);
        Assert.Equal(0, stats1.Evictions);

        // Add one more — should trigger eviction
        await cache.EmbedAsync("four");

        var stats2 = cache.GetStats();
        Assert.True(stats2.CurrentSize <= 3);
        Assert.True(stats2.Evictions >= 1);
    }

    [Fact]
    public async Task LfuEviction_EvictsLeastFrequent()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 3);

        // Add 3 items
        await cache.EmbedAsync("hot");   // Will be accessed more
        await cache.EmbedAsync("warm");  // Accessed once more
        await cache.EmbedAsync("cold");  // Never re-accessed — should be evicted

        // Boost frequency of "hot" and "warm"
        await cache.EmbedAsync("hot");
        await cache.EmbedAsync("hot");
        await cache.EmbedAsync("warm");

        inner.Reset();

        // Force eviction by adding a 4th item
        await cache.EmbedAsync("newcomer");

        // "cold" should have been evicted (lowest frequency = 1)
        inner.Reset();
        await cache.EmbedAsync("cold"); // Should miss cache
        Assert.Equal(1, inner.SingleEmbedCalls);

        // "hot" should still be cached
        inner.Reset();
        await cache.EmbedAsync("hot"); // Should hit cache
        Assert.Equal(0, inner.SingleEmbedCalls);
    }

    [Fact]
    public async Task HitRate_CalculatesCorrectly()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        await cache.EmbedAsync("test"); // miss
        await cache.EmbedAsync("test"); // hit
        await cache.EmbedAsync("test"); // hit
        await cache.EmbedAsync("test"); // hit

        var stats = cache.GetStats();
        Assert.Equal(3, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(0.75, stats.HitRate, 2);
    }

    [Fact]
    public async Task EmptyAndNullText_BypassesCache()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        await cache.EmbedAsync("");
        await cache.EmbedAsync("");

        // Empty strings bypass cache, so inner is called each time
        Assert.Equal(2, inner.SingleEmbedCalls);
        Assert.Equal(0, cache.GetStats().CurrentSize);
    }

    [Fact]
    public void EmbeddingDimension_DelegatesToInner()
    {
        var inner = new CountingEmbeddingService(dimension: 768);
        using var cache = new CachingEmbeddingService(inner);

        Assert.Equal(768, cache.EmbeddingDimension);
    }

    [Fact]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmpty()
    {
        var inner = new CountingEmbeddingService();
        using var cache = new CachingEmbeddingService(inner, maxEntries: 100);

        var result = await cache.EmbedBatchAsync(Array.Empty<string>());
        Assert.Empty(result);
        Assert.Equal(0, inner.BatchEmbedCalls);
    }
}

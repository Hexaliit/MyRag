using System.Diagnostics;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.Benchmarks;

/// <summary>
/// Benchmarks for the retrieval pipeline, focusing on quality anchor embedding caching
/// and scoring throughput.
/// </summary>
public class RetrievalBenchmarks(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private StorageService _storage = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _storage.DisposeAsync();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task B4_ScoreItems_QualityAnchorCalls()
    {
        var counting = new CountingEmbeddingService();
        var pipeline = new RetrievalPipeline(counting, _storage);

        var queryEmbedding = await counting.EmbedAsync("test query");
        counting.Reset(); // Don't count setup calls

        var items = CreateTestItems(5);
        var options = new ScoringOptions
        {
            Query = "test query about RAG pipelines",
            QueryEmbedding = queryEmbedding
        };

        // Call ScoreItemsAsync 3 times
        for (var i = 0; i < 3; i++)
        {
            await pipeline.ScoreItemsAsync(items, options);
        }

        output.WriteLine($"After 3 ScoreItemsAsync calls: " +
                         $"{counting.BatchEmbedCalls} batch, " +
                         $"{counting.SingleEmbedCalls} single, " +
                         $"{counting.TotalTextsEmbedded} total");

        // After optimization: anchor embeds should be cached, so only 1 batch call total
        // for both anchors (on first invocation), not 2 single calls per invocation.
        // The remaining single calls are 0 (items come with pre-set embeddings here).
        counting.SingleEmbedCalls.Should().BeLessThanOrEqualTo(1,
            "quality anchors should be cached after first ScoreItemsAsync call — " +
            "at most 1 batch call for both anchors across all invocations");
    }

    [Fact]
    public async Task B10_ScoreItems_Throughput()
    {
        var counting = new CountingEmbeddingService();
        var pipeline = new RetrievalPipeline(counting, _storage);

        var queryEmbedding = await counting.EmbedAsync("test query about AI");
        var items = CreateTestItems(20);

        var options = new ScoringOptions
        {
            Query = "test query about AI and machine learning",
            QueryEmbedding = queryEmbedding
        };

        // Warm up
        await pipeline.ScoreItemsAsync(CreateTestItems(5), options);

        const int iterations = 50;
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            // Reset items (scoring mutates RelevanceScore)
            foreach (var item in items)
            {
                item.RelevanceScore = 0;
                item.Embedding = null;
            }
            await pipeline.ScoreItemsAsync(items, options);
        }

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"ScoreItemsAsync (20 items): {opsPerSec:N0} ops/sec ({sw.ElapsedMilliseconds}ms total)");
    }

    private static List<ContentItem> CreateTestItems(int count) =>
        Enumerable.Range(0, count).Select(i => new ContentItem
        {
            Id = $"bench-{i}",
            Source = "benchmark",
            Title = $"Test Document {i}",
            Content = $"This is test content for document {i} about retrieval augmented generation.",
            Url = $"https://example.com/{i}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-i),
            FetchedAt = DateTimeOffset.UtcNow
        }).ToList();
}

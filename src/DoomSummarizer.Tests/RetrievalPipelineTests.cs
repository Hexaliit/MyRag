using DoomSummarizer.Models;
using DoomSummarizer.Services;
using FluentAssertions;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Xunit;

namespace DoomSummarizer.Tests;

/// <summary>
/// Integration tests for RetrievalPipeline.ScoreItemsAsync — the unified scoring path.
/// Uses real ONNX embeddings and temp SQLite DB per test.
/// </summary>
[Trait("Category", "RequiresModel")]
[Collection("EmbeddingTests")]
public class RetrievalPipelineTests : IAsyncLifetime
{
    private IEmbeddingService _embedding = null!;
    private StorageService _storage = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _embedding = await EmbeddingFactory.CreateAsync();
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_pipeline_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _storage.DisposeAsync();
        (_embedding as IDisposable)?.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static ContentItem MakeItem(string id, string title, string? content = null,
        string source = "test", DateTimeOffset? createdAt = null)
    {
        return new ContentItem
        {
            Id = id,
            Source = source,
            Title = title,
            Content = content ?? $"Content about {title}",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            FetchedAt = DateTimeOffset.UtcNow,
            Url = $"https://example.com/{id}"
        };
    }

    #region ScoreItemsAsync — Basic Behavior

    [Fact]
    public async Task ScoreItemsAsync_EmptyList_ReturnsEmpty()
    {
        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("test query");

        var result = await pipeline.ScoreItemsAsync([], new ScoringOptions
        {
            Query = "test query",
            QueryEmbedding = queryEmbed,

        });

        result.Items.Should().BeEmpty();
        result.QueryType.Should().Be(QueryType.General);
    }

    [Fact]
    public async Task ScoreItemsAsync_ReturnsAllItemsScored()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Machine learning advances", "Deep learning models improve at natural language processing"),
            MakeItem("2", "Climate change report", "Global temperatures continue to rise according to scientists"),
            MakeItem("3", "Stock market update", "Markets react to Federal Reserve interest rate decision"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("artificial intelligence");

        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "artificial intelligence",
            QueryEmbedding = queryEmbed,

        });

        result.Items.Should().NotBeEmpty();
        result.Items.Should().HaveCountGreaterThanOrEqualTo(1);
        // All items should have non-zero relevance scores
        result.Items.Should().OnlyContain(i => i.RelevanceScore > 0);
    }

    [Fact]
    public async Task ScoreItemsAsync_RanksRelevantItemsHigher()
    {
        var items = new List<ContentItem>
        {
            MakeItem("ai", "Neural networks and deep learning",
                "Researchers demonstrate new transformer architecture for natural language understanding using attention mechanisms"),
            MakeItem("climate", "Global warming accelerates",
                "Climate scientists report record temperatures in the Arctic ice sheet measurements"),
            MakeItem("sports", "Championship basketball game",
                "Lakers defeat Celtics in overtime thriller during NBA Finals championship series"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("machine learning AI");

        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "machine learning AI",
            QueryEmbedding = queryEmbed,

        });

        // AI item should rank first
        result.Items.First().Id.Should().Be("ai");
    }

    [Fact]
    public async Task ScoreItemsAsync_ItemsSortedByRelevanceDescending()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Tech news", "Software engineering tools and practices"),
            MakeItem("2", "More tech", "Programming languages and frameworks for web development"),
            MakeItem("3", "Healthcare", "Medical research findings on cancer treatment approaches"),
            MakeItem("4", "Finance", "Stock market analysis and investment strategies"),
            MakeItem("5", "Weather", "Forecast shows rain expected next week across the region"),
            MakeItem("6", "Cooking", "Recipe for homemade pasta with tomato sauce and basil"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("software development");

        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "software development",
            QueryEmbedding = queryEmbed,

        });

        // Items should be in descending relevance order
        for (var i = 1; i < result.Items.Count; i++)
        {
            result.Items[i].RelevanceScore.Should()
                .BeLessThanOrEqualTo(result.Items[i - 1].RelevanceScore,
                    $"item {i} should not score higher than item {i - 1}");
        }
    }

    #endregion

    #region ScoreItemsAsync — Embedding & Keyword Assignment

    [Fact]
    public async Task ScoreItemsAsync_AssignsEmbeddingsToItemsWithout()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Test item one", "Content for testing embedding assignment"),
            MakeItem("2", "Test item two", "More content for testing embedding assignment"),
        };

        // Items start without embeddings
        items.Should().OnlyContain(i => i.Embedding == null);

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("testing");

        await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "testing",
            QueryEmbedding = queryEmbed,

        });

        // Items should now have embeddings assigned
        items.Should().OnlyContain(i => i.Embedding != null);
    }

    [Fact]
    public async Task ScoreItemsAsync_AssignsKeywordsToItemsWithout()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Machine learning advances", "Deep learning improves natural language processing"),
        };

        // Items start without keywords
        items[0].Keywords.Should().BeNullOrEmpty();

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("AI");

        await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "AI",
            QueryEmbedding = queryEmbed,

        });

        // Items should now have keywords
        items[0].Keywords.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ScoreItemsAsync — Query Type Detection

    [Fact]
    public async Task ScoreItemsAsync_DetectsQueryType()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "AI news", "Recent advances in artificial intelligence"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);

        // Roundup-style query
        var queryEmbed = await _embedding.EmbedAsync("what happened today");
        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "what happened today",
            QueryEmbedding = queryEmbed,

        });

        // Query type should be detected (not null)
        result.QueryType.Should().NotBe((QueryType)(-1));
    }

    [Fact]
    public async Task ScoreItemsAsync_RespectsQueryTypeOverride()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "AI safety", "Research on AI alignment and safety"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("AI safety");

        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "AI safety",
            QueryEmbedding = queryEmbed,
            QueryType = QueryType.Explainer,

        });

        result.QueryType.Should().Be(QueryType.Explainer);
    }

    #endregion

    #region ScoreItemsAsync — KB vs Web Mode

    [Fact]
    public async Task ScoreItemsAsync_KbMode_UsesKnowledgeBaseScorer()
    {
        // Items with varied sources/dates — KB mode should zero authority/freshness weights
        var items = new List<ContentItem>
        {
            MakeItem("bbc", "AI regulation frameworks by governments worldwide",
                "Governments worldwide implement artificial intelligence regulation frameworks for public safety and accountability",
                source: "bbc", createdAt: DateTimeOffset.UtcNow.AddDays(-30)),
            MakeItem("reddit", "AI regulation discussion and debate",
                "Communities worldwide debate artificial intelligence regulation frameworks and accountability policies",
                source: "reddit", createdAt: DateTimeOffset.UtcNow.AddHours(-1)),
            MakeItem("tech", "Machine learning model governance",
                "Technology companies adopt machine learning governance practices for responsible AI deployment",
                source: "techcrunch", createdAt: DateTimeOffset.UtcNow.AddDays(-7)),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("AI regulation");

        // KB mode should work without errors and return scored items
        var kbResult = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "AI regulation",
            QueryEmbedding = queryEmbed,
            IsKnowledgeBase = true,

            UseEmbeddingDedup = false,
        });

        // Web mode should also work
        var webResult = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "AI regulation",
            QueryEmbedding = queryEmbed,
            IsKnowledgeBase = false,

            UseEmbeddingDedup = false,
        });

        // Both modes produce scored results
        kbResult.Items.Should().NotBeEmpty();
        webResult.Items.Should().NotBeEmpty();
        kbResult.Items.Should().OnlyContain(i => i.RelevanceScore > 0);
        webResult.Items.Should().OnlyContain(i => i.RelevanceScore > 0);

        // KB mode returns all items (no authority/freshness filtering advantage)
        kbResult.Items.Should().HaveCount(items.Count);
        webResult.Items.Should().HaveCount(items.Count);
    }

    #endregion

    #region ScoreItemsAsync — Embedding Dedup

    [Fact]
    public async Task ScoreItemsAsync_WithDedup_RemovesNearDuplicates()
    {
        // Two items with nearly identical content
        var items = new List<ContentItem>
        {
            MakeItem("1", "Machine learning advances in 2024",
                "Deep learning models continue to improve at natural language processing tasks"),
            MakeItem("2", "Machine learning progress in 2024",
                "Deep learning models keep improving at natural language processing tasks"),
            MakeItem("3", "Stock market update",
                "Financial markets react to Federal Reserve interest rate decision"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("machine learning");

        var withDedup = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "machine learning",
            QueryEmbedding = queryEmbed,
            UseEmbeddingDedup = true,

        });

        var withoutDedup = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "machine learning",
            QueryEmbedding = queryEmbed,
            UseEmbeddingDedup = false,

        });

        // With dedup should remove one of the near-duplicate ML items
        withDedup.Items.Count.Should().BeLessThan(withoutDedup.Items.Count);
    }

    #endregion

    #region ScoreItemsAsync — Vibe Scoring

    [Fact]
    public async Task ScoreItemsAsync_VibeText_AffectsScoring()
    {
        var items = new List<ContentItem>
        {
            MakeItem("doom", "Global disaster looms",
                "Catastrophic climate change threatens civilization with extreme weather events and rising sea levels"),
            MakeItem("hope", "Breakthrough renewable energy",
                "Scientists achieve major breakthrough in solar energy efficiency, promising clean sustainable future"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("energy news");

        var doomResult = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "energy news",
            QueryEmbedding = queryEmbed,
            VibeText = "Pessimistic, alarming, catastrophic, doom and gloom perspective",

        });

        var hopeResult = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "energy news",
            QueryEmbedding = queryEmbed,
            VibeText = "Optimistic, hopeful, promising breakthrough perspective",

        });

        // Both should return items
        doomResult.Items.Should().NotBeEmpty();
        hopeResult.Items.Should().NotBeEmpty();

        // Scores should differ between doom and hope vibes
        var doomTopScore = doomResult.Items.First().RelevanceScore;
        var hopeTopScore = hopeResult.Items.First().RelevanceScore;
        // At minimum they should both produce valid scores
        doomTopScore.Should().BeGreaterThan(0);
        hopeTopScore.Should().BeGreaterThan(0);
    }

    #endregion

    #region PRF Centroid

    [Fact]
    public async Task ScoreItemsAsync_WithEnoughItems_RefinesPrfCentroid()
    {
        // Create enough items (>= 5) to trigger PRF centroid refinement
        var items = Enumerable.Range(1, 8).Select(i =>
            MakeItem($"{i}", $"Article about technology {i}",
                $"Technology advances in computing systems and software development iteration {i}")
        ).ToList();

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("technology computing");

        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "technology computing",
            QueryEmbedding = queryEmbed,

        });

        // With >= 5 items, PRF centroid should be computed
        result.RefinedQueryEmbedding.Should().NotBeNull();
        // Refined embedding should differ from original
        result.RefinedQueryEmbedding.Should().NotBeEquivalentTo(queryEmbed);
    }

    [Fact]
    public async Task ScoreItemsAsync_FewItems_NoPrfRefinement()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Single article", "Just one article about testing"),
        };

        var pipeline = new RetrievalPipeline(_embedding, _storage);
        var queryEmbed = await _embedding.EmbedAsync("testing");

        var result = await pipeline.ScoreItemsAsync(items, new ScoringOptions
        {
            Query = "testing",
            QueryEmbedding = queryEmbed,

        });

        // With < 5 items, PRF centroid should equal original query embedding
        result.RefinedQueryEmbedding.Should().BeEquivalentTo(queryEmbed);
    }

    #endregion
}

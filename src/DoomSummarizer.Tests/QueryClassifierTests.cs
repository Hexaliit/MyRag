using DoomSummarizer.Models;
using DoomSummarizer.Services;
using DoomSummarizer.Tests.Benchmarks;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for the multi-match weighted voting QueryClassifier (Phase 2).
///     Unit tests use CountingEmbeddingService (hash-based, no semantic meaning).
///     Integration tests use real ONNX embeddings for semantic accuracy verification.
/// </summary>
public class QueryClassifierTests
{
    [Fact]
    public void LoadAllExemplars_ReturnsExpectedCount()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        exemplars.Should().HaveCountGreaterThanOrEqualTo(400);
    }

    [Fact]
    public void LoadAllExemplars_HasNewTypes()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        var types = exemplars.Select(e => e.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        types.Should().Contain("composite");
        types.Should().Contain("search_only");
        types.Should().Contain("trend");
        types.Should().Contain("news");
    }

    [Fact]
    public void LoadAllExemplars_HasVibeExemplars()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        var vibes = exemplars
            .Where(e => e.Vibe != null)
            .Select(e => e.Vibe!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        vibes.Should().Contain("doom");
        vibes.Should().Contain("hopeful");
        vibes.Should().Contain("snarky");
        vibes.Should().Contain("funny");
        vibes.Should().Contain("upbeat");
        vibes.Should().Contain("friendly");
        vibes.Should().Contain("toon");
    }

    [Fact]
    public void LoadAllExemplars_HasComplexityExemplars()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        exemplars.Where(e => e.Complexity == "complex").Should().HaveCountGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void LoadAllExemplars_HasCompositeExemplars()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        exemplars.Where(e => e.Type == "composite").Should().HaveCountGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void LoadAllExemplars_NoDuplicateQuestions()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        var duplicates = exemplars.Select(e => e.Question.ToLowerInvariant())
            .GroupBy(q => q).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        duplicates.Should().BeEmpty("duplicate questions waste embedding capacity");
    }

    [Fact]
    public void LoadAllExemplars_CoversAllExpectedTopics()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        var topics = exemplars.Select(e => e.Topic).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var expected in new[] { "technology", "ai", "programming", "entertainment", "science",
            "health", "business", "finance", "politics", "world", "environment", "sports", "space",
            "security", "crime", "flooding", "gaming" })
            topics.Should().Contain(expected);
    }

    [Fact]
    public async Task ClassifyAsync_WhenNotInitialized_ReturnsEmptyResult()
    {
        var classifier = new QueryClassifier();
        var result = await classifier.ClassifyAsync("test query");
        result.Categories.Should().BeEmpty();
        result.QueryType.Should().Be("roundup");
    }

    [Fact]
    public async Task ClassifyAsync_WithFakeEmbeddings_ReturnsCategories()
    {
        var embedding = new CountingEmbeddingService();
        var classifier = new QueryClassifier();
        await classifier.InitializeAsync(embedding);

        var result = await classifier.ClassifyAsync("test query");
        result.Should().NotBeNull();
        result.TopMatches.Should().NotBeNull();
    }

    [Fact]
    public async Task ClassifyAsync_IsDeterministic()
    {
        var embedding = new CountingEmbeddingService();
        var classifier = new QueryClassifier();
        await classifier.InitializeAsync(embedding);

        var r1 = await classifier.ClassifyAsync("latest technology news");
        var r2 = await classifier.ClassifyAsync("latest technology news");

        r1.BestMatchScore.Should().Be(r2.BestMatchScore);
        r1.QueryType.Should().Be(r2.QueryType);
    }

    [Fact]
    public async Task ClassifyAsync_TopMatches_LimitedToFive()
    {
        var embedding = new CountingEmbeddingService();
        var classifier = new QueryClassifier();
        await classifier.InitializeAsync(embedding);

        var result = await classifier.ClassifyAsync("What's the latest news?");
        result.TopMatches.Should().HaveCountLessThanOrEqualTo(5);
    }
}

// ══════════════════════════════════════════════════════════════════
// ── Integration tests: real ONNX embeddings for semantic tests ──
// ══════════════════════════════════════════════════════════════════

/// <summary>
///     Shared fixture that initializes the ONNX embedding service once for all integration tests.
/// </summary>
public class OnnxClassifierFixture : IAsyncLifetime, IDisposable
{
    public IEmbeddingService? Embedding { get; private set; }
    public QueryClassifier? Classifier { get; private set; }
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            Embedding = await EmbeddingFactory.CreateAsync();
            Classifier = new QueryClassifier();
            await Classifier.InitializeAsync(Embedding);
            IsAvailable = Classifier.IsInitialized;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        (Embedding as IDisposable)?.Dispose();
    }
}

[Trait("Category", "Integration")]
public class QueryClassifierOnnxTests(OnnxClassifierFixture fixture) : IClassFixture<OnnxClassifierFixture>
{
    [Fact]
    public void InitializesWithAllExemplars()
    {
        if (!fixture.IsAvailable) return;
        fixture.Classifier!.ExemplarCount.Should().BeGreaterThanOrEqualTo(250);
    }

    [Theory]
    [InlineData("What's the latest tech news?", "technology")]
    [InlineData("Show me AI and machine learning news", "ai")]
    [InlineData("Latest .NET framework updates", "programming")]
    [InlineData("Celebrity gossip and entertainment", "entertainment")]
    [InlineData("Recent physics research breakthroughs", "science")]
    [InlineData("Health news and medical research", "health")]
    [InlineData("Stock market and investment news", "finance")]
    [InlineData("Political developments today", "politics")]
    [InlineData("International headlines", "world")]
    [InlineData("Climate change updates", "environment")]
    [InlineData("Football results this weekend", "sports")]
    [InlineData("Rocket launches and space missions", "space")]
    [InlineData("Data breaches and cybersecurity", "security")]
    [InlineData("Video game releases this month", "gaming")]
    public async Task TopicDetection_CorrectPrimaryTopic(string query, string expectedTopic)
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync(query);

        result.Categories.Should().ContainKey(expectedTopic);
        var topTopics = result.Categories.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        topTopics.Should().Contain(expectedTopic,
            $"query '{query}' should classify as '{expectedTopic}' but got [{string.Join(", ", topTopics)}]");
    }

    [Theory]
    [InlineData("How do I set up a Docker container?", "howto")]
    [InlineData("Compare React vs Angular", "comparison")]
    [InlineData("What is quantum entanglement?", "deep_dive")]  // closest exemplar is deep_dive variant
    [InlineData("Latest tech news roundup", "roundup")]
    [InlineData("In-depth analysis of AI safety", "deep_dive")]
    public async Task TypeDetection_CorrectType(string query, string expectedType)
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync(query);

        result.QueryType.Should().Be(expectedType,
            $"query '{query}' should detect type '{expectedType}' but got '{result.QueryType}'");
    }

    [Theory]
    [InlineData("Give me the most depressing news", "doom")]
    [InlineData("Doom-scroll today's terrible headlines", "doom")]
    [InlineData("Sarcastic take on today's news", "snarky")]
    [InlineData("Show me positive uplifting stories", "hopeful")]
    public async Task VibeDetection_DetectsVibe(string query, string expectedVibe)
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync(query);

        result.Vibe.Should().Be(expectedVibe,
            $"query '{query}' should detect vibe '{expectedVibe}' but got '{result.Vibe}'");
    }

    [Theory]
    [InlineData("Tech news and also what's in politics")]
    [InlineData("AI developments this week and compare the top models")]
    [InlineData("Get me business news and find out what's new in AI")]
    public async Task CompositeDetection_DetectsComposite(string query)
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync(query);

        result.IsComposite.Should().BeTrue(
            $"query '{query}' should be composite. Type={result.QueryType} ({result.QueryTypeConfidence:F2})");
    }

    [Theory]
    [InlineData("What are the second-order effects of rising interest rates on tech?")]
    [InlineData("How might the EU AI Act affect open source development in practice?")]
    public async Task ComplexDetection_DetectsComplex(string query)
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync(query);

        result.IsComplex.Should().BeTrue(
            $"query '{query}' should be complex but wasn't");
    }

    [Theory]
    [InlineData("What time is it in Tokyo?", "search_only")]
    [InlineData("Define ontological", "search_only")]
    [InlineData("What's the population of France?", "search_only")]
    [InlineData("Convert 100 dollars to euros", "search_only")]
    public async Task SearchOnlyDetection_DetectsSearchOnly(string query, string expectedType)
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync(query);

        result.QueryType.Should().Be(expectedType,
            $"query '{query}' should detect type '{expectedType}' but got '{result.QueryType}'");
    }

    [Fact]
    public async Task StrongMatchRate_AboveSeventy()
    {
        if (!fixture.IsAvailable) return;
        var testQueries = new[]
        {
            "latest tech news", "AI developments today", "celebrity gossip",
            "how to set up Docker", "compare React vs Vue", "stock market update",
            "what happened in Congress", "international headlines today",
            "climate change news", "sports scores today", "cybersecurity alerts",
            "doom scroll worst news", "AI news and politics too",
            "what time is it in London", "quantum physics breakthrough",
            "video game releases", "latest .NET updates", "space exploration news",
            "entertainment and movies", "health and wellness tips"
        };

        var strongMatches = 0;
        foreach (var query in testQueries)
        {
            var result = await fixture.Classifier!.ClassifyAsync(query);
            if (result.BestMatchScore >= 0.55)
                strongMatches++;
        }

        var rate = (double)strongMatches / testQueries.Length;
        rate.Should().BeGreaterThanOrEqualTo(0.70,
            $"strong match rate was {rate:P0} ({strongMatches}/{testQueries.Length})");
    }

    [Fact]
    public async Task WeightedVoting_GenericQueryMatchesMultipleTopics()
    {
        if (!fixture.IsAvailable) return;
        var result = await fixture.Classifier!.ClassifyAsync("latest news roundup");

        result.Categories.Count.Should().BeGreaterThan(1);
        result.TopMatches.Select(m => m.Topic).Distinct().Count().Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Determinism_SameQuerySameResults()
    {
        if (!fixture.IsAvailable) return;
        var results = new QueryClassification[3];
        for (var i = 0; i < 3; i++)
            results[i] = await fixture.Classifier!.ClassifyAsync("What's happening in AI today?");

        for (var i = 1; i < 3; i++)
        {
            results[i].BestMatchScore.Should().Be(results[0].BestMatchScore);
            results[i].QueryType.Should().Be(results[0].QueryType);
            results[i].IsComposite.Should().Be(results[0].IsComposite);
            results[i].Vibe.Should().Be(results[0].Vibe);
        }
    }

    [Fact]
    public async Task CentroidFilter_DoesNotDropRelevantTopics()
    {
        if (!fixture.IsAvailable) return;
        var nicheQueries = new (string Query, string ExpectedTopic)[]
        {
            ("flood warnings in my area", "flooding"),
            ("local crime statistics", "crime"),
            ("new drug approvals FDA", "pharma"),
            ("satirical news roundup", "satire"),
            ("latest gaming news", "gaming"),
            ("food safety recalls", "food"),
            ("airline delays and travel news", "transport"),
        };

        var failures = new List<string>();
        foreach (var (query, expectedTopic) in nicheQueries)
        {
            var result = await fixture.Classifier!.ClassifyAsync(query);
            if (!result.Categories.ContainsKey(expectedTopic))
                failures.Add($"'{query}' missing topic '{expectedTopic}' (got: {string.Join(", ", result.Categories.Keys)})");
        }

        failures.Should().BeEmpty(string.Join("\n", failures));
    }
}

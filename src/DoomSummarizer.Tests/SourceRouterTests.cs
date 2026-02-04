using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

public class SourceRouterTests
{
    private static SourceRouter CreateRouter()
    {
        var config = new SourceRoutingConfig
        {
            Sources = new Dictionary<string, SourceDefinition>
            {
                ["google_news"] = new() { Type = "google_news_rss", Description = "Google News", Search = true },
                ["bbc"] = new()
                {
                    Type = "rss", Description = "BBC News",
                    Feeds = new Dictionary<string, List<string>>
                    {
                        ["default"] = ["https://feeds.bbci.co.uk/news/rss.xml"],
                        ["health"] = ["https://feeds.bbci.co.uk/news/health/rss.xml"],
                        ["technology"] = ["https://feeds.bbci.co.uk/news/technology/rss.xml"],
                        ["science"] = ["https://feeds.bbci.co.uk/news/science_and_environment/rss.xml"]
                    }
                },
                ["hn"] = new() { Type = "hackernews", Description = "Hacker News" },
                ["reddit"] = new() { Type = "reddit", Description = "Reddit" }
            },
            Routing = new Dictionary<string, RoutingRule>
            {
                ["health"] = new()
                    { Sources = ["google_news", "bbc"], BbcCategory = "health", GoogleNewsTopic = "HEALTH" },
                ["technology"] = new()
                {
                    Sources = ["hn", "reddit", "google_news", "bbc"], BbcCategory = "technology",
                    GoogleNewsTopic = "TECHNOLOGY"
                },
                ["default"] = new() { Sources = ["google_news", "bbc"] }
            },
            TopicKeywords = new Dictionary<string, List<string>>
            {
                ["health"] = ["health", "medical", "pharmaceutical", "pharma", "drug", "vaccine"],
                ["technology"] = ["tech", "software", "programming", "code", "ai"]
            }
        };

        return new SourceRouter(config);
    }

    [Theory]
    [InlineData("new pharmaceutical news", "health")]
    [InlineData("latest health updates", "health")]
    [InlineData("drug approval news", "health")]
    [InlineData("vaccine development", "health")]
    [InlineData("pharmaceutical research", "health")]
    public async Task DetectTopic_HealthKeywords_ReturnsHealth(string query, string expected)
    {
        var router = CreateRouter();
        (await router.DetectTopicAsync(query)).Should().Be(expected);
    }

    [Theory]
    [InlineData("latest tech news", "technology")]
    [InlineData("ai developments", "technology")]
    [InlineData("programming languages", "technology")]
    [InlineData("software updates", "technology")]
    public async Task DetectTopic_TechKeywords_ReturnsTechnology(string query, string expected)
    {
        var router = CreateRouter();
        (await router.DetectTopicAsync(query)).Should().Be(expected);
    }

    [Fact]
    public async Task DetectTopic_NoMatch_ReturnsDefault()
    {
        var router = CreateRouter();
        (await router.DetectTopicAsync("random gibberish foobar")).Should().Be("default");
    }

    [Fact]
    public async Task Route_HealthQuery_ReturnsHealthSources()
    {
        var router = CreateRouter();
        var result = await router.RouteAsync("pharmaceutical news");

        result.Topic.Should().Be("health");
        result.Sources.Should().Contain("google_news");
        result.Sources.Should().Contain("bbc");
        result.BbcCategory.Should().Be("health");
        result.GoogleNewsTopic.Should().Be("HEALTH");
    }

    [Fact]
    public async Task Route_TechQuery_ReturnsTechSources()
    {
        var router = CreateRouter();
        var result = await router.RouteAsync("software engineering");

        result.Topic.Should().Be("technology");
        result.Sources.Should().Contain("hn");
        result.Sources.Should().Contain("reddit");
        result.BbcCategory.Should().Be("technology");
    }

    [Fact]
    public async Task Route_UnknownQuery_UsesDefaultRouting()
    {
        var router = CreateRouter();
        var result = await router.RouteAsync("random topic xyz");

        result.Topic.Should().Be("default");
        result.Sources.Should().Contain("google_news");
        result.BbcCategory.Should().BeNull();
    }

    [Fact]
    public void GetFeeds_WithCategory_ReturnsCategorySpecificFeed()
    {
        var router = CreateRouter();
        var feeds = router.GetFeeds("bbc", "health");

        feeds.Should().ContainSingle()
            .Which.Should().Contain("health");
    }

    [Fact]
    public void GetFeeds_WithoutCategory_ReturnsDefaultFeed()
    {
        var router = CreateRouter();
        var feeds = router.GetFeeds("bbc");

        feeds.Should().ContainSingle()
            .Which.Should().Contain("rss.xml");
    }

    [Fact]
    public void GetFeeds_UnknownCategory_FallsBackToDefault()
    {
        var router = CreateRouter();
        var feeds = router.GetFeeds("bbc", "nonexistent");

        feeds.Should().ContainSingle()
            .Which.Should().Contain("rss.xml");
    }

    [Fact]
    public void GetFeeds_UnknownSource_ReturnsEmpty()
    {
        var router = CreateRouter();
        var feeds = router.GetFeeds("unknownsource");

        feeds.Should().BeEmpty();
    }

    [Fact]
    public void GetSource_KnownSource_ReturnsDefinition()
    {
        var router = CreateRouter();
        var source = router.GetSource("bbc");

        source.Should().NotBeNull();
        source!.Type.Should().Be("rss");
        source.Description.Should().Be("BBC News");
    }

    [Fact]
    public void GetSource_UnknownSource_ReturnsNull()
    {
        var router = CreateRouter();
        router.GetSource("unknown").Should().BeNull();
    }

    [Fact]
    public void Load_EmbeddedYaml_LoadsSuccessfully()
    {
        // This tests loading from the actual embedded resource
        var router = SourceRouter.Load();

        router.AllSources.Should().Contain("google_news");
        router.AllSources.Should().Contain("bbc");
        router.AllSources.Should().Contain("hn");
        router.AllTopics.Should().Contain("health");
        router.AllTopics.Should().Contain("technology");
        router.AllTopics.Should().Contain("default");
    }

    [Fact]
    public async Task Load_EmbeddedYaml_HealthRoutingWorks()
    {
        var router = SourceRouter.Load();
        var result = await router.RouteAsync("pharmaceutical drug approval");

        // Should route to pharma or health topic
        result.Topic.Should().BeOneOf("health", "pharma");
        result.Sources.Should().Contain("google_news");
        result.BbcCategory.Should().Be("health");
        result.GoogleNewsTopic.Should().Be("HEALTH");
    }

    [Fact]
    public async Task DetectTopic_MultiWordKeyword_MatchesSubstring()
    {
        var config = new SourceRoutingConfig
        {
            TopicKeywords = new Dictionary<string, List<string>>
            {
                ["finance"] = ["wall street", "stock market"]
            },
            Sources = new Dictionary<string, SourceDefinition>(),
            Routing = new Dictionary<string, RoutingRule>
            {
                ["default"] = new() { Sources = ["google_news"] }
            }
        };

        var router = new SourceRouter(config);
        (await router.DetectTopicAsync("what's happening on wall street today")).Should().Be("finance");
    }

    [Fact]
    public async Task DetectTopic_MultipleTopicMatches_ReturnsBestMatch()
    {
        var router = CreateRouter();
        // "pharmaceutical tech" has 1 health keyword and 1 tech keyword
        // both should score 1, but pharma is more specific
        var topic = await router.DetectTopicAsync("pharmaceutical technology drug software");

        // health has 2 matches (pharmaceutical, drug), technology has 2 (technology, software)
        // Either could win - both are valid
        topic.Should().BeOneOf("health", "technology");
    }

    // --- ScoreSource tests (using real embedded YAML) ---

    [Fact]
    public void ScoreSource_Wikipedia_HighForQA_LowForNews()
    {
        var router = SourceRouter.Load();
        var qaCategories = new Dictionary<string, double> { ["science"] = 0.8 };

        var qaScore = router.ScoreSource("wikipedia", "qa", qaCategories);
        var newsScore = router.ScoreSource("wikipedia", "news", qaCategories);

        qaScore.Should().BeGreaterThan(0.5, "wikipedia should score high for QA");
        newsScore.Should().BeLessThan(0.4, "wikipedia should score low for news");
        qaScore.Should().BeGreaterThan(newsScore, "wikipedia QA score should beat news score");
    }

    [Fact]
    public void ScoreSource_GNews_HighForNews_LowForQA()
    {
        var router = SourceRouter.Load();
        var categories = new Dictionary<string, double> { ["science"] = 0.8 };

        var newsScore = router.ScoreSource("google_news", "news", categories);
        var qaScore = router.ScoreSource("google_news", "qa", categories);

        newsScore.Should().BeGreaterThan(0.5, "gnews should score high for news");
        qaScore.Should().BeLessThan(newsScore, "gnews QA score should be lower than news");
    }

    [Fact]
    public void ScoreSource_DuckDuckGo_HighForQA()
    {
        var router = SourceRouter.Load();
        var categories = new Dictionary<string, double> { ["science"] = 0.8 };

        var qaScore = router.ScoreSource("duckduckgo", "qa", categories);

        qaScore.Should().BeGreaterThan(0.5, "duckduckgo should score high for QA");
    }

    [Fact]
    public void ScoreSource_UnknownSource_ReturnsZero()
    {
        var router = SourceRouter.Load();
        var categories = new Dictionary<string, double> { ["technology"] = 0.9 };

        router.ScoreSource("nonexistent_source", "news", categories).Should().Be(0.0);
    }

    [Fact]
    public void ScoreSource_AllSourcesInRange()
    {
        var router = SourceRouter.Load();
        var categories = new Dictionary<string, double> { ["technology"] = 0.9 };

        foreach (var source in router.AllSources)
        {
            foreach (var intent in new[] { "news", "qa", "research", "roundup", "howto" })
            {
                var score = router.ScoreSource(source, intent, categories);
                score.Should().BeInRange(0.0, 1.0, $"{source}/{intent} should be in [0,1]");
            }
        }
    }

    [Fact]
    public void HasCapability_TechOnly_Works()
    {
        var router = SourceRouter.Load();

        router.HasCapability("hn", "tech_only").Should().BeTrue("hn should be tech_only");
        router.HasCapability("lobsters", "tech_only").Should().BeTrue("lobsters should be tech_only");
        router.HasCapability("bbc", "tech_only").Should().BeFalse("bbc should not be tech_only");
        router.HasCapability("wikipedia", "tech_only").Should().BeFalse("wikipedia should not be tech_only");
    }

    [Fact]
    public void HasCapability_Archive_Works()
    {
        var router = SourceRouter.Load();

        router.HasCapability("wikipedia", "archive").Should().BeTrue("wikipedia should be archive");
        router.HasCapability("arxiv", "archive").Should().BeTrue("arxiv should be archive");
        router.HasCapability("bbc", "archive").Should().BeFalse("bbc should not be archive");
    }

    [Fact]
    public void HasCapability_Knowledge_Works()
    {
        var router = SourceRouter.Load();

        router.HasCapability("wikipedia", "knowledge").Should().BeTrue("wikipedia should have knowledge");
        router.HasCapability("duckduckgo", "knowledge").Should().BeTrue("duckduckgo should have knowledge");
        router.HasCapability("bbc", "knowledge").Should().BeFalse("bbc should not have knowledge");
    }

    [Fact]
    public void ScoreSource_NullIntentAffinity_FallsBackToTypeDefaults()
    {
        // Source with no intent_affinity should use type-derived fallback
        var config = new SourceRoutingConfig
        {
            Sources = new Dictionary<string, SourceDefinition>
            {
                ["test_rss"] = new() { Type = "rss", Description = "Test RSS" }
            },
            Routing = new Dictionary<string, RoutingRule>
            {
                ["default"] = new() { Sources = ["test_rss"] }
            },
            TopicKeywords = new Dictionary<string, List<string>>()
        };

        var router = new SourceRouter(config);
        var categories = new Dictionary<string, double> { ["default"] = 0.5 };

        var newsScore = router.ScoreSource("test_rss", "news", categories);
        var qaScore = router.ScoreSource("test_rss", "qa", categories);

        newsScore.Should().BeGreaterThan(0.0, "type-derived fallback should produce non-zero scores");
        qaScore.Should().BeGreaterThan(0.0, "type-derived fallback should produce non-zero scores");
        newsScore.Should().BeGreaterThan(qaScore, "RSS type should favor news over QA by default");
    }

    [Fact]
    public void GetSourcesWithCapability_ReturnsCorrectSources()
    {
        var router = SourceRouter.Load();

        var techOnly = router.GetSourcesWithCapability("tech_only").ToList();
        techOnly.Should().Contain("hn");
        techOnly.Should().Contain("lobsters");
        techOnly.Should().NotContain("bbc");
        techOnly.Should().NotContain("wikipedia");

        var knowledge = router.GetSourcesWithCapability("knowledge").ToList();
        knowledge.Should().Contain("wikipedia");
        knowledge.Should().Contain("duckduckgo");
    }
}
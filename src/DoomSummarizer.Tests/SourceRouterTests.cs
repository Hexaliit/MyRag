using DoomSummarizer.Models;
using DoomSummarizer.Services;
using FluentAssertions;
using Xunit;

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
                ["health"] = new() { Sources = ["google_news", "bbc"], BbcCategory = "health", GoogleNewsTopic = "HEALTH" },
                ["technology"] = new() { Sources = ["hn", "reddit", "google_news", "bbc"], BbcCategory = "technology", GoogleNewsTopic = "TECHNOLOGY" },
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
    public void DetectTopic_HealthKeywords_ReturnsHealth(string query, string expected)
    {
        var router = CreateRouter();
        router.DetectTopic(query).Should().Be(expected);
    }

    [Theory]
    [InlineData("latest tech news", "technology")]
    [InlineData("ai developments", "technology")]
    [InlineData("programming languages", "technology")]
    [InlineData("software updates", "technology")]
    public void DetectTopic_TechKeywords_ReturnsTechnology(string query, string expected)
    {
        var router = CreateRouter();
        router.DetectTopic(query).Should().Be(expected);
    }

    [Fact]
    public void DetectTopic_NoMatch_ReturnsDefault()
    {
        var router = CreateRouter();
        router.DetectTopic("random gibberish foobar").Should().Be("default");
    }

    [Fact]
    public void Route_HealthQuery_ReturnsHealthSources()
    {
        var router = CreateRouter();
        var result = router.Route("pharmaceutical news");

        result.Topic.Should().Be("health");
        result.Sources.Should().Contain("google_news");
        result.Sources.Should().Contain("bbc");
        result.BbcCategory.Should().Be("health");
        result.GoogleNewsTopic.Should().Be("HEALTH");
    }

    [Fact]
    public void Route_TechQuery_ReturnsTechSources()
    {
        var router = CreateRouter();
        var result = router.Route("software engineering");

        result.Topic.Should().Be("technology");
        result.Sources.Should().Contain("hn");
        result.Sources.Should().Contain("reddit");
        result.BbcCategory.Should().Be("technology");
    }

    [Fact]
    public void Route_UnknownQuery_UsesDefaultRouting()
    {
        var router = CreateRouter();
        var result = router.Route("random topic xyz");

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
    public void Load_EmbeddedYaml_HealthRoutingWorks()
    {
        var router = SourceRouter.Load();
        var result = router.Route("pharmaceutical drug approval");

        // Should route to pharma or health topic
        result.Topic.Should().BeOneOf("health", "pharma");
        result.Sources.Should().Contain("google_news");
        result.BbcCategory.Should().Be("health");
        result.GoogleNewsTopic.Should().Be("HEALTH");
    }

    [Fact]
    public void DetectTopic_MultiWordKeyword_MatchesSubstring()
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
        router.DetectTopic("what's happening on wall street today").Should().Be("finance");
    }

    [Fact]
    public void DetectTopic_MultipleTopicMatches_ReturnsBestMatch()
    {
        var router = CreateRouter();
        // "pharmaceutical tech" has 1 health keyword and 1 tech keyword
        // both should score 1, but pharma is more specific
        var topic = router.DetectTopic("pharmaceutical technology drug software");

        // health has 2 matches (pharmaceutical, drug), technology has 2 (technology, software)
        // Either could win - both are valid
        topic.Should().BeOneOf("health", "technology");
    }
}

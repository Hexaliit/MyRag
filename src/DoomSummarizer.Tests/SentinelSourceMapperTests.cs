using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for SentinelSourceMapper: structured intent → source list mapping.
///     Verifies that category weights are correctly converted to CLI source identifiers.
/// </summary>
public class SentinelSourceMapperTests
{
    private static SourceRouter GetRouter()
    {
        return SourceRouter.Load();
    }

    // --- Category-based source selection ---

    [Fact]
    public void MapToSources_TechnologyCategory_IncludesTechSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double> { ["technology"] = 0.9 },
            Tone = "neutral",
            SearchQueries = ["technology news"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "latest tech news");

        // Should include tech-specific sources
        sources.Should().Contain(s => s.StartsWith("gnews:"));
        sources.Should().Contain(s => s == "hn" || s == "ars" || s == "verge" || s == "lobsters" ||
                                      s == "techcrunch" || s == "theregister");
    }

    [Fact]
    public void MapToSources_EntertainmentCategory_ExcludesTechSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "qa",
            Categories = new Dictionary<string, double> { ["entertainment"] = 0.9 },
            Tone = "neutral",
            SearchQueries = ["SNL host this week"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "Who is hosting SNL this week?");

        sources.Should().Contain(s => s.StartsWith("gnews:"));
        // Entertainment should get BBC entertainment, not tech sources
        sources.Should().Contain(s => s == "bbc:entertainment" || s == "guardian" || s == "npr");
        sources.Should().NotContain("hn");
        sources.Should().NotContain("lobsters");
        sources.Should().NotContain("techcrunch");
    }

    [Fact]
    public void MapToSources_HealthCategory_GetsHealthSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double> { ["health"] = 0.8, ["science"] = 0.3 },
            Tone = "neutral",
            SearchQueries = ["pharmaceutical news"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "pharmaceutical news");

        sources.Should().Contain(s => s.StartsWith("gnews:"));
        sources.Should().Contain(s => s == "bbc:health" || s == "guardian" || s == "npr" || s == "sciencedaily");
        sources.Should().NotContain("hn");
    }

    [Fact]
    public void MapToSources_BusinessCategory_GetsBusinessSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double> { ["business"] = 0.9 },
            Tone = "neutral",
            SearchQueries = ["stock market news"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "stock market updates");

        sources.Should().Contain(s => s.StartsWith("gnews:"));
        sources.Should().Contain(s => s == "bbc:business" || s == "guardian" || s == "reuters" || s == "npr");
    }

    // --- Explicit source handling ---

    [Fact]
    public void MapToSources_ExplicitSources_AlwaysIncluded()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double> { ["entertainment"] = 0.9 },
            Tone = "doom",
            SearchQueries = ["news"],
            ExplicitSources = ["hn", "bbc"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "doom scroll hacker news and bbc");

        // User-named sources always included even if category doesn't match
        sources.Should().Contain("hn");
        sources.Should().Contain("bbc");
    }

    // --- Intent-based filtering ---

    [Fact]
    public void MapToSources_RoundupIntent_ExcludesArchiveSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "roundup",
            Categories = new Dictionary<string, double> { ["technology"] = 0.9 },
            Tone = "neutral",
            TimeSensitivity = "today",
            SearchQueries = ["technology news today"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "what happened today in tech");

        // Roundup should exclude archive sources
        sources.Should().NotContain(s => s.StartsWith("arxiv"));
        sources.Should().NotContain("wikipedia");
    }

    [Fact]
    public void MapToSources_ResearchIntent_IncludesArxiv()
    {
        var intent = new SentinelIntent
        {
            Intent = "research",
            Categories = new Dictionary<string, double> { ["ai"] = 0.9, ["science"] = 0.5 },
            Tone = "neutral",
            SearchQueries = ["large language model scaling"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "latest AI research papers");

        sources.Should().Contain(s => s.StartsWith("arxiv"));
    }

    // --- NER enrichment (search queries only, no outlet routing) ---

    [Fact]
    public void MapToSources_NerEntities_AddsGnewsQueryNotOutlets()
    {
        var intent = new SentinelIntent
        {
            Intent = "qa",
            Categories = new Dictionary<string, double> { ["entertainment"] = 0.9 },
            Tone = "neutral",
            SearchQueries = ["SNL host"]
        };

        var nerContext = new QueryNerContext
        {
            RawQuery = "Who is hosting SNL?",
            Entities = [new NerEntity { Text = "SNL", Type = "ORG", Confidence = 0.85f }],
            Organizations = ["SNL"],
            EntityQueries =
            [
                new EntitySearchQuery
                {
                    Query = "\"SNL\"",
                    EntityText = "SNL",
                    EntityType = "ORG",
                    PreferredSources = ["gnews", "search"]
                }
            ]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "Who is hosting SNL?", nerContext);

        // NER should add gnews entity query
        sources.Should().Contain(s => s.Contains("SNL"));
        // NER should NOT add business outlets for an entertainment query
        sources.Should().NotContain("bbc:business");
        sources.Should().NotContain("reuters");
    }

    // --- Empty/default handling ---

    [Fact]
    public void MapToSources_EmptyCategories_GetsDefaultSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "howto",
            Categories = new Dictionary<string, double>(), // Empty
            Tone = "friendly",
            SearchQueries = ["muffin recipe"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "fun muffin recipe");

        // Should still have some sources (gnews + defaults)
        sources.Count.Should().BeGreaterThanOrEqualTo(3);
        sources.Should().Contain(s => s.StartsWith("gnews:"));
    }

    [Fact]
    public void MapToSources_CapsAtMaxSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double>
            {
                ["technology"] = 0.9,
                ["business"] = 0.8,
                ["health"] = 0.7,
                ["science"] = 0.6,
                ["politics"] = 0.5
            },
            Tone = "neutral",
            SearchQueries = ["general news"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "everything happening now");

        // Should cap at max sources (10)
        sources.Should().HaveCountLessThanOrEqualTo(10);
    }

    // --- Multi-category weighting ---

    [Fact]
    public void MapToSources_MultipleCategories_HigherWeightGetsMoreSources()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double>
            {
                ["ai"] = 0.9,
                ["business"] = 0.3
            },
            Tone = "neutral",
            SearchQueries = ["AI industry news"]
        };

        var sources = SentinelSourceMapper.MapToSources(intent, GetRouter(), "AI industry news");

        // Primary category (ai=0.9) should get more sources than secondary (business=0.3)
        var aiSources = new HashSet<string> { "hn", "verge", "techcrunch", "theregister", "ars" };
        var businessSources = new HashSet<string> { "reuters", "cnn" };

        var aiCount = sources.Count(s => aiSources.Contains(s));
        var businessCount = sources.Count(s => businessSources.Contains(s));

        // AI should have more representation
        aiCount.Should().BeGreaterThanOrEqualTo(businessCount);
    }

    // --- SentinelIntent model tests ---

    [Fact]
    public void SentinelIntent_DefaultValues_AreReasonable()
    {
        var intent = new SentinelIntent();

        intent.Intent.Should().Be("news");
        intent.Tone.Should().Be("neutral");
        intent.TimeSensitivity.Should().Be("any");
        intent.Limit.Should().Be(20);
        intent.Categories.Should().BeEmpty();
        intent.SearchQueries.Should().BeEmpty();
        intent.ExplicitSources.Should().BeEmpty();
    }

    // --- ToInterpretedPrompt conversion ---

    [Fact]
    public void ToInterpretedPrompt_ConvertsCorrectly()
    {
        var intent = new SentinelIntent
        {
            Intent = "news",
            Categories = new Dictionary<string, double> { ["entertainment"] = 0.8, ["humor"] = 0.4 },
            Tone = "funny",
            TimeSensitivity = "today",
            SearchQueries = ["entertainment news"],
            Limit = 30
        };

        var result = SentinelSourceMapper.ToInterpretedPrompt(intent, GetRouter(), "funny entertainment news");

        result.Vibe.Should().Be("funny");
        result.Limit.Should().Be(30);
        result.Sources.Should().NotBeEmpty();
        result.Topics.Should().Contain("entertainment");
        result.SentinelIntent.Should().BeSameAs(intent);
        result.RawPrompt.Should().Be("funny entertainment news");
    }

    // --- YAML source mapping ---

    [Fact]
    public void MapYamlSourceToCli_MapsCorrectly()
    {
        var routing = new RoutingResult
        {
            Topic = "technology",
            Sources = ["hn", "bbc"],
            BbcCategory = "technology"
        };

        SentinelSourceMapper.MapYamlSourceToCli("bbc", routing, "tech news")
            .Should().Be("bbc:technology");

        SentinelSourceMapper.MapYamlSourceToCli("hn", routing, "tech news")
            .Should().Be("hn");

        SentinelSourceMapper.MapYamlSourceToCli("guardian", routing, "tech news")
            .Should().Be("guardian");
    }

    [Fact]
    public void MapYamlSourceToCli_SearchSources_IncludeQuery()
    {
        var routing = new RoutingResult
        {
            Topic = "default",
            Sources = ["google_news", "duckduckgo"]
        };

        var gnews = SentinelSourceMapper.MapYamlSourceToCli("google_news", routing, "muffin recipe");
        gnews.Should().StartWith("gnews:");
        gnews.Should().Contain("muffin");

        var ddg = SentinelSourceMapper.MapYamlSourceToCli("duckduckgo", routing, "muffin recipe");
        ddg.Should().StartWith("search:");
        ddg.Should().Contain("muffin");
    }
}
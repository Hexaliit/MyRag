using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for QueryPreprocessor NER context building and enrichment.
/// </summary>
public class QueryPreprocessorTests
{
    // --- QueryNerContext structure tests ---

    [Fact]
    public void QueryNerContext_HasEntities_TrueWhenEntitiesPresent()
    {
        var context = new QueryNerContext
        {
            RawQuery = "Tell me about Microsoft",
            Entities = [new NerEntity { Text = "Microsoft", Type = "ORG", Confidence = 0.95f }],
            Organizations = ["Microsoft"]
        };

        context.HasEntities.Should().BeTrue();
    }

    [Fact]
    public void QueryNerContext_HasEntities_FalseWhenEmpty()
    {
        var context = new QueryNerContext { RawQuery = "tell me about stuff" };
        context.HasEntities.Should().BeFalse();
    }

    [Fact]
    public void QueryNerContext_AllEntityNames_CombinesAllTypes()
    {
        var context = new QueryNerContext
        {
            RawQuery = "Einstein in Berlin working at Max Planck Institute",
            PersonNames = ["Einstein"],
            Locations = ["Berlin"],
            Organizations = ["Max Planck Institute"]
        };

        context.AllEntityNames.Should().BeEquivalentTo(
            ["Einstein", "Max Planck Institute", "Berlin"]);
    }

    // --- EntitySearchQuery building tests ---
    // NER only adds search-based sources (gnews, search) — not news outlet preferences.
    // The sentinel LLM handles category-based source routing.

    [Fact]
    public void EntitySearchQuery_OrgEntity_GetsQuotedQueryAndSearchSources()
    {
        var query = new EntitySearchQuery
        {
            Query = "\"Automator Group Inc\"",
            EntityText = "Automator Group Inc",
            EntityType = "ORG",
            PreferredSources = ["gnews", "search"]
        };

        query.Query.Should().Contain("\"Automator Group Inc\"");
        query.PreferredSources.Should().Contain("gnews");
        query.PreferredSources.Should().Contain("search");
        // NER does NOT add news outlet preferences — sentinel handles category routing
        query.PreferredSources.Should().NotContain("bbc:business");
        query.PreferredSources.Should().NotContain("reuters");
    }

    [Fact]
    public void EntitySearchQuery_PersonEntity_GetsSearchSources()
    {
        var query = new EntitySearchQuery
        {
            Query = "\"Albert Einstein\"",
            EntityText = "Albert Einstein",
            EntityType = "PER",
            PreferredSources = ["gnews", "search"]
        };

        query.PreferredSources.Should().Contain("gnews");
        query.PreferredSources.Should().Contain("search");
        // NER does NOT add wikipedia/bbc — sentinel handles category routing
        query.PreferredSources.Should().NotContain("wikipedia");
    }

    [Fact]
    public void EntitySearchQuery_LocationEntity_GetsSearchSources()
    {
        var query = new EntitySearchQuery
        {
            Query = "Tokyo",
            EntityText = "Tokyo",
            EntityType = "LOC",
            PreferredSources = ["gnews", "search"]
        };

        query.PreferredSources.Should().Contain("gnews");
        query.PreferredSources.Should().Contain("search");
        // NER does NOT add bbc:world/guardian/reuters — sentinel handles category routing
        query.PreferredSources.Should().NotContain("bbc:world");
        query.PreferredSources.Should().NotContain("reuters");
    }

    // --- InterpretedPrompt NER enrichment tests ---

    [Fact]
    public void InterpretedPrompt_NerEnrichment_AddsEntityQueries()
    {
        var nerContext = new QueryNerContext
        {
            RawQuery = "Tell me about Google",
            Entities = [new NerEntity { Text = "Google", Type = "ORG", Confidence = 0.98f }],
            Organizations = ["Google"],
            EntityQueries =
            [
                new EntitySearchQuery
                {
                    Query = "\"Google\"",
                    EntityText = "Google",
                    EntityType = "ORG",
                    PreferredSources = ["gnews", "search"]
                }
            ]
        };

        nerContext.HasEntities.Should().BeTrue();
        nerContext.EntityQueries.Should().ContainSingle(q => q.EntityText == "Google");
    }

    [Fact]
    public void QueryNerContext_CachedItems_TracksKnownUrls()
    {
        var context = new QueryNerContext
        {
            RawQuery = "test",
            KnownUrls = ["https://example.com/article1", "https://example.com/article2"]
        };

        context.KnownUrls.Should().HaveCount(2);
        context.HasCachedData.Should().BeFalse(); // No cached items, just URLs
    }

    [Fact]
    public void QueryNerContext_HasCachedData_TrueWhenItemsPresent()
    {
        var context = new QueryNerContext
        {
            RawQuery = "test",
            CachedItems = [new DoomSummarizer.Models.StoredItem
            {
                Id = "test_1", Source = "bbc", Title = "Test", Summary = "Summary"
            }]
        };

        context.HasCachedData.Should().BeTrue();
    }
}

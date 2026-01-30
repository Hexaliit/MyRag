using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for EntityProfileService - weighted entity profile computation.
/// Requires ONNX embedding model to be available.
/// </summary>
[Trait("Category", "RequiresModel")]
public class EntityProfileServiceTests : IAsyncLifetime
{
    private IEmbeddingService _embedding = null!;

    public async Task InitializeAsync()
    {
        _embedding = await EmbeddingFactory.CreateAsync();
    }

    public Task DisposeAsync()
    {
        (_embedding as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ComputeProfile_EmptyEntities_ReturnsEmptyArray()
    {
        var service = new EntityProfileService(_embedding);
        var result = await service.ComputeProfileAsync([], new Dictionary<string, int>(), 100);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ComputeProfile_SingleEntity_ReturnsNormalizedVector()
    {
        var service = new EntityProfileService(_embedding);
        var entities = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("org_test123", "OpenAI", 0.9f, 3)
        };
        var docCounts = new Dictionary<string, int> { ["org_test123"] = 5 };

        var result = await service.ComputeProfileAsync(entities, docCounts, 100);

        Assert.Equal(384, result.Length);
        // Verify L2 normalized (magnitude should be ~1)
        var magnitude = Math.Sqrt(result.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public async Task ComputeProfile_RareEntityHasHigherWeight()
    {
        var service = new EntityProfileService(_embedding);

        // Rare entity (appears in 1/100 docs) vs common entity (appears in 50/100 docs)
        var rareEntity = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("per_rare", "Elon Musk", 0.9f, 1)
        };
        var commonEntity = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("loc_common", "California", 0.9f, 1)
        };

        var docCountsRare = new Dictionary<string, int> { ["per_rare"] = 1 };
        var docCountsCommon = new Dictionary<string, int> { ["loc_common"] = 50 };

        var rareProfile = await service.ComputeProfileAsync(rareEntity, docCountsRare, 100);
        var commonProfile = await service.ComputeProfileAsync(commonEntity, docCountsCommon, 100);

        // Both should be valid 384-dim vectors
        Assert.Equal(384, rareProfile.Length);
        Assert.Equal(384, commonProfile.Length);

        // They should be different (rare vs common IDF)
        var similarity = VectorMath.CosineSimilarity(rareProfile, commonProfile);
        Assert.NotEqual(1.0f, similarity); // Different entities = different vectors
    }

    [Fact]
    public async Task ComputeProfile_LowConfidenceEntityStillContributes()
    {
        var service = new EntityProfileService(_embedding);

        // Very low confidence entity (should be clamped to floor 0.2)
        var lowConfEntity = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("org_lowconf", "Microsoft", 0.05f, 1) // Below floor
        };
        var docCounts = new Dictionary<string, int> { ["org_lowconf"] = 10 };

        var result = await service.ComputeProfileAsync(lowConfEntity, docCounts, 100);

        // Should still produce a valid profile (not zeroed out)
        Assert.Equal(384, result.Length);
        Assert.True(result.Any(x => x != 0), "Profile should not be all zeros despite low confidence");
    }

    [Fact]
    public async Task ComputeProfile_SaturatingTF_PreventsDominance()
    {
        var service = new EntityProfileService(_embedding);

        // Entity with very high mention count (boilerplate-like)
        var manyMentions = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("loc_eu", "European Union", 0.9f, 100) // Many mentions
        };
        var fewMentions = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("loc_eu", "European Union", 0.9f, 2) // Few mentions
        };
        var docCounts = new Dictionary<string, int> { ["loc_eu"] = 20 };

        var profileMany = await service.ComputeProfileAsync(manyMentions, docCounts, 100);
        var profileFew = await service.ComputeProfileAsync(fewMentions, docCounts, 100);

        // Both should be valid
        Assert.Equal(384, profileMany.Length);
        Assert.Equal(384, profileFew.Length);

        // With saturating TF (1 + log), difference should be modest, not 50x
        var similarity = VectorMath.CosineSimilarity(profileMany, profileFew);
        Assert.InRange(similarity, 0.99, 1.01); // Nearly identical after L2 norm
    }

    [Fact]
    public async Task ComputeProfileWithExplain_ReturnsTopEntities()
    {
        var service = new EntityProfileService(_embedding);
        var entities = new List<(string entityId, string name, string type, float confidence, int mentions)>
        {
            ("org_openai", "OpenAI", "ORG", 0.9f, 5),
            ("per_altman", "Sam Altman", "PER", 0.85f, 3),
            ("loc_sf", "San Francisco", "LOC", 0.8f, 2)
        };
        var docCounts = new Dictionary<string, int>
        {
            ["org_openai"] = 10,
            ["per_altman"] = 5,
            ["loc_sf"] = 50
        };

        var (profile, topEntities) = await service.ComputeProfileWithExplainAsync(entities, docCounts, 100);

        Assert.Equal(384, profile.Length);
        Assert.NotEmpty(topEntities);
        Assert.True(topEntities.Count <= 5);
        // Top entity should have highest weight
        Assert.True(topEntities[0].weight >= topEntities[^1].weight);
    }

    [Fact]
    public async Task ComputeAggregateProfile_MultipleProfiles_CombinesCorrectly()
    {
        var service = new EntityProfileService(_embedding);

        // Create two different profiles
        var entities1 = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("org_openai", "OpenAI", 0.9f, 3)
        };
        var entities2 = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("org_anthropic", "Anthropic", 0.9f, 3)
        };
        var docCounts = new Dictionary<string, int>
        {
            ["org_openai"] = 10,
            ["org_anthropic"] = 8
        };

        var profile1 = await service.ComputeProfileAsync(entities1, docCounts, 100);
        var profile2 = await service.ComputeProfileAsync(entities2, docCounts, 100);

        var aggregate = service.ComputeAggregateProfile([profile1, profile2]);

        Assert.Equal(384, aggregate.Length);
        // Aggregate should be L2 normalized
        var magnitude = Math.Sqrt(aggregate.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public async Task ComputeQueryProfile_ShortQuery_ReturnsValidProfile()
    {
        var service = new EntityProfileService(_embedding);
        var queryEntities = new List<(string name, string type, float confidence)>
        {
            ("OpenAI", "ORG", 0.9f),
            ("Sam Altman", "PER", 0.85f)
        };
        var docCounts = new Dictionary<string, int>
        {
            [KnowledgeGraphService.GenerateEntityId("OpenAI", "ORG")] = 10,
            [KnowledgeGraphService.GenerateEntityId("Sam Altman", "PER")] = 5
        };

        var result = await service.ComputeQueryProfileAsync(queryEntities, docCounts, 100);

        Assert.Equal(384, result.Length);
        var magnitude = Math.Sqrt(result.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public async Task TypeWeights_ORGAndPERGetBoost()
    {
        var service = new EntityProfileService(_embedding);

        // Same entity name, different types
        var orgEntity = new List<(string entityId, string name, string type, float confidence, int mentions)>
        {
            ("org_apple", "Apple", "ORG", 0.9f, 1)
        };
        var locEntity = new List<(string entityId, string name, string type, float confidence, int mentions)>
        {
            ("loc_apple", "Apple", "LOC", 0.9f, 1)
        };
        var docCounts = new Dictionary<string, int>
        {
            ["org_apple"] = 10,
            ["loc_apple"] = 10
        };

        var (orgProfile, orgTopEntities) = await service.ComputeProfileWithExplainAsync(orgEntity, docCounts, 100);
        var (locProfile, locTopEntities) = await service.ComputeProfileWithExplainAsync(locEntity, docCounts, 100);

        // ORG should have higher weight than LOC (1.2 vs 1.0)
        Assert.True(orgTopEntities[0].weight > locTopEntities[0].weight);
    }

    // Entity Type Inference tests moved to EntityTypeInferenceTests.cs
    // (they don't require ONNX model and can run in CI)
}

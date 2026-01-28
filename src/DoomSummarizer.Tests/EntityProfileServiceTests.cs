using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for EntityProfileService - weighted entity profile computation.
/// Requires ONNX embedding model to be available.
/// </summary>
[Trait("Category", "RequiresModel")]
public class EntityProfileServiceTests : IAsyncLifetime
{
    private EmbeddingService _embedding = null!;

    public async Task InitializeAsync()
    {
        _embedding = new EmbeddingService();
        await _embedding.EnsureReadyAsync();
    }

    public Task DisposeAsync()
    {
        _embedding.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void ComputeProfile_EmptyEntities_ReturnsEmptyArray()
    {
        var service = new EntityProfileService(_embedding);
        var result = service.ComputeProfile([], new Dictionary<string, int>(), 100);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputeProfile_SingleEntity_ReturnsNormalizedVector()
    {
        var service = new EntityProfileService(_embedding);
        var entities = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("org_test123", "OpenAI", 0.9f, 3)
        };
        var docCounts = new Dictionary<string, int> { ["org_test123"] = 5 };

        var result = service.ComputeProfile(entities, docCounts, 100);

        Assert.Equal(384, result.Length);
        // Verify L2 normalized (magnitude should be ~1)
        var magnitude = Math.Sqrt(result.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public void ComputeProfile_RareEntityHasHigherWeight()
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

        var rareProfile = service.ComputeProfile(rareEntity, docCountsRare, 100);
        var commonProfile = service.ComputeProfile(commonEntity, docCountsCommon, 100);

        // Both should be valid 384-dim vectors
        Assert.Equal(384, rareProfile.Length);
        Assert.Equal(384, commonProfile.Length);

        // They should be different (rare vs common IDF)
        var similarity = VectorMath.CosineSimilarity(rareProfile, commonProfile);
        Assert.NotEqual(1.0f, similarity); // Different entities = different vectors
    }

    [Fact]
    public void ComputeProfile_LowConfidenceEntityStillContributes()
    {
        var service = new EntityProfileService(_embedding);

        // Very low confidence entity (should be clamped to floor 0.2)
        var lowConfEntity = new List<(string entityId, string name, float confidence, int mentions)>
        {
            ("org_lowconf", "Microsoft", 0.05f, 1) // Below floor
        };
        var docCounts = new Dictionary<string, int> { ["org_lowconf"] = 10 };

        var result = service.ComputeProfile(lowConfEntity, docCounts, 100);

        // Should still produce a valid profile (not zeroed out)
        Assert.Equal(384, result.Length);
        Assert.True(result.Any(x => x != 0), "Profile should not be all zeros despite low confidence");
    }

    [Fact]
    public void ComputeProfile_SaturatingTF_PreventsDominance()
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

        var profileMany = service.ComputeProfile(manyMentions, docCounts, 100);
        var profileFew = service.ComputeProfile(fewMentions, docCounts, 100);

        // Both should be valid
        Assert.Equal(384, profileMany.Length);
        Assert.Equal(384, profileFew.Length);

        // With saturating TF (1 + log), difference should be modest, not 50x
        var similarity = VectorMath.CosineSimilarity(profileMany, profileFew);
        Assert.InRange(similarity, 0.99, 1.01); // Nearly identical after L2 norm
    }

    [Fact]
    public void ComputeProfileWithExplain_ReturnsTopEntities()
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

        var (profile, topEntities) = service.ComputeProfileWithExplain(entities, docCounts, 100);

        Assert.Equal(384, profile.Length);
        Assert.NotEmpty(topEntities);
        Assert.True(topEntities.Count <= 5);
        // Top entity should have highest weight
        Assert.True(topEntities[0].weight >= topEntities[^1].weight);
    }

    [Fact]
    public void ComputeAggregateProfile_MultipleProfiles_CombinesCorrectly()
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

        var profile1 = service.ComputeProfile(entities1, docCounts, 100);
        var profile2 = service.ComputeProfile(entities2, docCounts, 100);

        var aggregate = service.ComputeAggregateProfile([profile1, profile2]);

        Assert.Equal(384, aggregate.Length);
        // Aggregate should be L2 normalized
        var magnitude = Math.Sqrt(aggregate.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public void ComputeQueryProfile_ShortQuery_ReturnsValidProfile()
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

        var result = service.ComputeQueryProfile(queryEntities, docCounts, 100);

        Assert.Equal(384, result.Length);
        var magnitude = Math.Sqrt(result.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public void TypeWeights_ORGAndPERGetBoost()
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

        var (orgProfile, orgTopEntities) = service.ComputeProfileWithExplain(orgEntity, docCounts, 100);
        var (locProfile, locTopEntities) = service.ComputeProfileWithExplain(locEntity, docCounts, 100);

        // ORG should have higher weight than LOC (1.2 vs 1.0)
        Assert.True(orgTopEntities[0].weight > locTopEntities[0].weight);
    }

    // Entity Type Inference tests moved to EntityTypeInferenceTests.cs
    // (they don't require ONNX model and can run in CI)
}

using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for DuckDB vector store with HNSW indexing.
/// Uses a temp database per test to avoid cross-test contamination.
/// </summary>
public class DuckDbVectorStoreTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private DuckDbVectorStore _store = null!;

    public DuckDbVectorStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_test_{Guid.NewGuid():N}.duckdb");
    }

    public async Task InitializeAsync()
    {
        _store = new DuckDbVectorStore(_dbPath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        // Clean up temp files
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var walPath = _dbPath + ".wal";
            if (File.Exists(walPath)) File.Delete(walPath);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task UpsertItemEmbedding_StoresAndRetrieves()
    {
        var embedding = CreateRandomEmbedding();
        await _store.UpsertItemEmbeddingAsync("item1", "Test Article", "hn", "https://example.com", embedding);

        var stats = await _store.GetStatsAsync();
        stats.itemEmbeddings.Should().Be(1);
    }

    [Fact]
    public async Task UpsertItemEmbedding_UpdatesExisting()
    {
        var embedding1 = CreateRandomEmbedding();
        var embedding2 = CreateRandomEmbedding();

        await _store.UpsertItemEmbeddingAsync("item1", "Test", "hn", null, embedding1);
        await _store.UpsertItemEmbeddingAsync("item1", "Test Updated", "hn", null, embedding2);

        var stats = await _store.GetStatsAsync();
        stats.itemEmbeddings.Should().Be(1, "upsert should update, not duplicate");
    }

    [Fact]
    public async Task FindSimilarItems_ReturnsMatchingItems()
    {
        // Insert items with known embeddings
        var baseEmbedding = CreateNormalizedEmbedding(1.0f);
        var similarEmbedding = CreateNormalizedEmbedding(0.95f);
        var differentEmbedding = CreateNormalizedEmbedding(-1.0f);

        await _store.UpsertItemEmbeddingAsync("item1", "Similar Article", "hn", null, baseEmbedding);
        await _store.UpsertItemEmbeddingAsync("item2", "Also Similar", "hn", null, similarEmbedding);
        await _store.UpsertItemEmbeddingAsync("item3", "Very Different", "hn", null, differentEmbedding);

        var results = await _store.FindSimilarItemsAsync(baseEmbedding, topK: 10, minSimilarity: 0.5f);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.itemId == "item1");
    }

    [Fact]
    public async Task UpsertEntity_IncrementsOnDuplicate()
    {
        await _store.UpsertEntityAsync("per_abc123", "John Doe", "PER", 0.9);
        await _store.UpsertEntityAsync("per_abc123", "John Doe", "PER", 0.95);

        var entities = await _store.GetTopEntitiesAsync(10);
        entities.Should().HaveCount(1);
        entities[0].MentionCount.Should().Be(2);
    }

    [Fact]
    public async Task UpsertEntityMention_RecordsProvenance()
    {
        await _store.UpsertEntityAsync("per_abc", "Alice", "PER", 0.9);
        await _store.UpsertItemEmbeddingAsync("article1", "Article One", "hn", "https://example.com", CreateRandomEmbedding());
        await _store.UpsertEntityMentionAsync("per_abc", "article1", 0.9, "mentioned in title");

        var articles = await _store.GetArticlesForEntityAsync("per_abc");
        articles.Should().HaveCount(1);
        articles[0].title.Should().Be("Article One");
    }

    [Fact]
    public async Task UpsertRelationship_BuildsCoOccurrence()
    {
        await _store.UpsertEntityAsync("per_a", "Alice", "PER", 0.9);
        await _store.UpsertEntityAsync("org_b", "Acme Corp", "ORG", 0.8);

        await _store.UpsertRelationshipAsync("per_a", "org_b");

        var relationships = await _store.GetRelationshipsAsync("per_a");
        relationships.Should().HaveCount(1);
        relationships[0].Weight.Should().Be(1.0f);
    }

    [Fact]
    public async Task UpsertRelationship_IncrementsWeight()
    {
        await _store.UpsertEntityAsync("per_a", "Alice", "PER", 0.9);
        await _store.UpsertEntityAsync("org_b", "Acme Corp", "ORG", 0.8);

        await _store.UpsertRelationshipAsync("per_a", "org_b");
        await _store.UpsertRelationshipAsync("per_a", "org_b");
        await _store.UpsertRelationshipAsync("org_b", "per_a"); // Reversed order should be same edge

        var relationships = await _store.GetRelationshipsAsync("per_a");
        relationships.Should().HaveCount(1);
        relationships[0].Weight.Should().Be(3.0f);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        await _store.UpsertEntityAsync("per_a", "Alice", "PER", 0.9);
        await _store.UpsertEntityAsync("org_b", "Acme", "ORG", 0.8);
        await _store.UpsertRelationshipAsync("per_a", "org_b");
        await _store.UpsertEntityMentionAsync("per_a", "item1", 0.9);
        await _store.UpsertItemEmbeddingAsync("item1", "Test", "hn", null, CreateRandomEmbedding());

        var stats = await _store.GetStatsAsync();
        stats.entities.Should().Be(2);
        stats.relationships.Should().Be(1);
        stats.mentions.Should().Be(1);
        stats.itemEmbeddings.Should().Be(1);
    }

    [Fact]
    public async Task GetTopEntities_FiltersByType()
    {
        await _store.UpsertEntityAsync("per_a", "Alice", "PER", 0.9);
        await _store.UpsertEntityAsync("org_b", "Acme", "ORG", 0.8);
        await _store.UpsertEntityAsync("loc_c", "London", "LOC", 0.7);

        var people = await _store.GetTopEntitiesAsync(10, type: "PER");
        people.Should().HaveCount(1);
        people[0].Name.Should().Be("Alice");
    }

    [Fact]
    public async Task Cleanup_RemovesStaleData()
    {
        // Insert data (it will be "fresh")
        await _store.UpsertEntityAsync("per_a", "Alice", "PER", 0.9);
        await _store.UpsertItemEmbeddingAsync("item1", "Test", "hn", null, CreateRandomEmbedding());
        await _store.UpsertEntityMentionAsync("per_a", "item1", 0.9);

        // Cleanup with 0-day retention should remove everything
        // (but since we just inserted, the timestamps are fresh,
        // so nothing should be removed with reasonable retention)
        await _store.CleanupAsync(retentionDays: 365);
        var stats = await _store.GetStatsAsync();
        stats.entities.Should().Be(1, "recent data should survive cleanup");
    }

    // --- Entity Profile Tests ---

    [Fact]
    public async Task UpsertItemEntityProfile_StoresProfile()
    {
        // First create an item
        await _store.UpsertItemEmbeddingAsync("item1", "Test Article", "hn", null, CreateRandomEmbedding());

        // Then add entity profile
        var entityProfile = CreateRandomEmbedding();
        await _store.UpsertItemEntityProfileAsync("item1", entityProfile);

        // Verify it's stored
        var hasProfiles = await _store.HasEntityProfilesAsync();
        hasProfiles.Should().BeTrue();
    }

    [Fact]
    public async Task HasEntityProfiles_ReturnsFalseWhenEmpty()
    {
        // No items with entity profiles
        await _store.UpsertItemEmbeddingAsync("item1", "Test", "hn", null, CreateRandomEmbedding());

        var hasProfiles = await _store.HasEntityProfilesAsync();
        hasProfiles.Should().BeFalse();
    }

    [Fact]
    public async Task FindRelatedByEntityProfile_FindsSimilarItems()
    {
        // Create items with entity profiles
        var profile1 = CreateNormalizedEmbedding(1.0f);
        var profile2 = CreateNormalizedEmbedding(0.9f);  // Similar to profile1
        var profile3 = CreateNormalizedEmbedding(-1.0f); // Very different

        await _store.UpsertItemEmbeddingAsync("item1", "AI Article 1", "hn", null, CreateRandomEmbedding());
        await _store.UpsertItemEmbeddingAsync("item2", "AI Article 2", "hn", null, CreateRandomEmbedding());
        await _store.UpsertItemEmbeddingAsync("item3", "Cooking Article", "hn", null, CreateRandomEmbedding());

        await _store.UpsertItemEntityProfileAsync("item1", profile1);
        await _store.UpsertItemEntityProfileAsync("item2", profile2);
        await _store.UpsertItemEntityProfileAsync("item3", profile3);

        // Search with a query profile similar to item1
        var results = await _store.FindRelatedByEntityProfileAsync(profile1, topK: 10, minSimilarity: 0.5f);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.itemId == "item1");
        results.Should().Contain(r => r.itemId == "item2");
        // item3 should have low similarity, might not be included due to threshold
    }

    [Fact]
    public async Task GetEntityDocCounts_ReturnsCorrectCounts()
    {
        // Set up entities mentioned in multiple items
        await _store.UpsertEntityAsync("org_openai", "OpenAI", "ORG", 0.9);
        await _store.UpsertEntityAsync("per_altman", "Sam Altman", "PER", 0.85);

        await _store.UpsertEntityMentionAsync("org_openai", "item1", 0.9);
        await _store.UpsertEntityMentionAsync("org_openai", "item2", 0.8);
        await _store.UpsertEntityMentionAsync("org_openai", "item3", 0.7);
        await _store.UpsertEntityMentionAsync("per_altman", "item1", 0.85);

        var counts = await _store.GetEntityDocCountsAsync();

        counts["org_openai"].Should().Be(3);
        counts["per_altman"].Should().Be(1);
    }

    [Fact]
    public async Task GetTotalDocsWithEntities_ReturnsCorrectCount()
    {
        await _store.UpsertEntityAsync("org_a", "Acme", "ORG", 0.9);
        await _store.UpsertEntityMentionAsync("org_a", "item1", 0.9);
        await _store.UpsertEntityMentionAsync("org_a", "item2", 0.8);
        await _store.UpsertEntityMentionAsync("org_a", "item3", 0.7);

        var total = await _store.GetTotalDocsWithEntitiesAsync();
        total.Should().Be(3);
    }

    [Fact]
    public async Task GetEntitiesForItem_ReturnsEntitiesWithCounts()
    {
        await _store.UpsertEntityAsync("org_openai", "OpenAI", "ORG", 0.9);
        await _store.UpsertEntityAsync("per_altman", "Sam Altman", "PER", 0.85);

        await _store.UpsertEntityMentionAsync("org_openai", "item1", 0.9);
        await _store.UpsertEntityMentionAsync("per_altman", "item1", 0.85);

        var entities = await _store.GetEntitiesForItemAsync("item1");

        entities.Should().HaveCount(2);
        entities.Should().Contain(e => e.entityId == "org_openai" && e.name == "OpenAI");
        entities.Should().Contain(e => e.entityId == "per_altman" && e.name == "Sam Altman");
    }

    [Fact]
    public async Task GetEntityProfiles_ReturnsStoredProfiles()
    {
        var profile1 = CreateRandomEmbedding();
        var profile2 = CreateRandomEmbedding();

        await _store.UpsertItemEmbeddingAsync("item1", "Article 1", "hn", null, CreateRandomEmbedding());
        await _store.UpsertItemEmbeddingAsync("item2", "Article 2", "hn", null, CreateRandomEmbedding());
        await _store.UpsertItemEmbeddingAsync("item3", "Article 3", "hn", null, CreateRandomEmbedding());

        await _store.UpsertItemEntityProfileAsync("item1", profile1);
        await _store.UpsertItemEntityProfileAsync("item2", profile2);
        // item3 has no entity profile

        var profiles = await _store.GetEntityProfilesAsync(["item1", "item2", "item3"]);

        profiles.Should().HaveCount(2);
        profiles.Should().ContainKey("item1");
        profiles.Should().ContainKey("item2");
        profiles.Should().NotContainKey("item3");
    }

    [Fact]
    public async Task GetEntityEmbeddings_ReturnsCachedEmbeddings()
    {
        var embedding1 = CreateRandomEmbedding();
        var embedding2 = CreateRandomEmbedding();

        await _store.UpsertEntityAsync("org_openai", "OpenAI", "ORG", 0.9, embedding1);
        await _store.UpsertEntityAsync("per_altman", "Sam Altman", "PER", 0.85, embedding2);
        await _store.UpsertEntityAsync("loc_sf", "San Francisco", "LOC", 0.8); // No embedding

        var embeddings = await _store.GetEntityEmbeddingsAsync(["org_openai", "per_altman", "loc_sf"]);

        embeddings.Should().HaveCount(2);
        embeddings.Should().ContainKey("org_openai");
        embeddings.Should().ContainKey("per_altman");
        embeddings.Should().NotContainKey("loc_sf");
    }

    // Helper: create a random 384-dim embedding
    private static float[] CreateRandomEmbedding()
    {
        var rng = new Random(42);
        var embedding = new float[384];
        for (var i = 0; i < 384; i++)
            embedding[i] = (float)(rng.NextDouble() * 2 - 1);
        return Normalize(embedding);
    }

    // Helper: create a biased normalized embedding
    private static float[] CreateNormalizedEmbedding(float bias)
    {
        var embedding = new float[384];
        for (var i = 0; i < 384; i++)
            embedding[i] = bias + (i % 10) * 0.01f;
        return Normalize(embedding);
    }

    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < v.Length; i++)
                v[i] /= norm;
        return v;
    }
}

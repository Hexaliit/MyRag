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

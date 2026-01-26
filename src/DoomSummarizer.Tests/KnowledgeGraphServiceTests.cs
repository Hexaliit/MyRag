using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for KnowledgeGraphService entity ingestion, co-occurrence, and display.
/// </summary>
public class KnowledgeGraphServiceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private DuckDbVectorStore _store = null!;
    private KnowledgeGraphService _graph = null!;

    public KnowledgeGraphServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_graph_test_{Guid.NewGuid():N}.duckdb");
    }

    public async Task InitializeAsync()
    {
        _store = new DuckDbVectorStore(_dbPath);
        await _store.InitializeAsync();
        _graph = new KnowledgeGraphService(_store);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var walPath = _dbPath + ".wal";
            if (File.Exists(walPath)) File.Delete(walPath);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task IngestEntities_StoresEntitiesAndMentions()
    {
        var item = CreateContentItem("item1", "Tech Giant Launches AI Product");
        var entities = new List<NerEntity>
        {
            new() { Text = "Tech Giant", Type = "ORG", Confidence = 0.9f },
            new() { Text = "AI Product", Type = "MISC", Confidence = 0.85f }
        };

        await _graph.IngestEntitiesAsync([(item, entities)]);

        var stats = await _store.GetStatsAsync();
        stats.entities.Should().Be(2);
        stats.mentions.Should().Be(2);
    }

    [Fact]
    public async Task IngestEntities_BuildsCoOccurrenceEdges()
    {
        var item = CreateContentItem("item1", "Alice at Acme Corp in London");
        var entities = new List<NerEntity>
        {
            new() { Text = "Alice", Type = "PER", Confidence = 0.9f },
            new() { Text = "Acme Corp", Type = "ORG", Confidence = 0.85f },
            new() { Text = "London", Type = "LOC", Confidence = 0.8f }
        };

        await _graph.IngestEntitiesAsync([(item, entities)]);

        var stats = await _store.GetStatsAsync();
        // 3 entities, C(3,2) = 3 co-occurrence edges
        stats.entities.Should().Be(3);
        stats.relationships.Should().Be(3);
    }

    [Fact]
    public async Task IngestEntities_DeduplicatesWithinArticle()
    {
        var item = CreateContentItem("item1", "Test");
        var entities = new List<NerEntity>
        {
            new() { Text = "Alice", Type = "PER", Confidence = 0.9f },
            new() { Text = "alice", Type = "PER", Confidence = 0.7f }, // Same entity, lower case
            new() { Text = "ALICE", Type = "PER", Confidence = 0.5f }  // Same entity, upper case
        };

        await _graph.IngestEntitiesAsync([(item, entities)]);

        var stats = await _store.GetStatsAsync();
        stats.entities.Should().Be(1, "case-insensitive dedup should merge");
    }

    [Fact]
    public async Task IngestEntities_AccumulatesAcrossRuns()
    {
        var item1 = CreateContentItem("item1", "First Article about Alice");
        var item2 = CreateContentItem("item2", "Second Article about Alice");

        await _graph.IngestEntitiesAsync([(item1, [new NerEntity { Text = "Alice", Type = "PER", Confidence = 0.9f }])]);
        await _graph.IngestEntitiesAsync([(item2, [new NerEntity { Text = "Alice", Type = "PER", Confidence = 0.85f }])]);

        var topEntities = await _store.GetTopEntitiesAsync(10);
        topEntities.Should().HaveCount(1);
        topEntities[0].MentionCount.Should().Be(2);
    }

    [Fact]
    public async Task IngestLinkedPageEntities_AppliesLowerConfidence()
    {
        var parentItem = CreateContentItem("parent1", "Parent Article");
        var linkedEntities = new List<NerEntity>
        {
            new() { Text = "Bob", Type = "PER", Confidence = 1.0f }
        };

        await _graph.IngestLinkedPageEntitiesAsync(parentItem, linkedEntities, "https://linked.example.com");

        var stats = await _store.GetStatsAsync();
        stats.entities.Should().Be(1);
        // The entity should be stored with adjusted confidence (0.7x)
    }

    [Fact]
    public async Task IndexItemEmbeddings_StoresInDuckDb()
    {
        var items = new List<ContentItem>
        {
            CreateContentItem("item1", "Article One"),
            CreateContentItem("item2", "Article Two"),
            CreateContentItem("item3", "Article Three (no embedding)")
        };
        items[0].Embedding = CreateRandomEmbedding();
        items[1].Embedding = CreateRandomEmbedding();
        // item3 has no embedding

        await _graph.IndexItemEmbeddingsAsync(items);

        var stats = await _store.GetStatsAsync();
        stats.itemEmbeddings.Should().Be(2, "only items with embeddings should be indexed");
    }

    [Fact]
    public async Task FindSimilar_ReturnsMatchingItems()
    {
        var embedding = CreateNormalizedEmbedding(1.0f);
        var item = CreateContentItem("item1", "Test Article");
        item.Embedding = embedding;

        await _graph.IndexItemEmbeddingsAsync([item]);

        var results = await _graph.FindSimilarAsync(embedding, topK: 5, minSimilarity: 0.5f);
        results.Should().NotBeEmpty();
        results[0].itemId.Should().Be("item1");
    }

    [Fact]
    public void GenerateEntityId_IsDeterministic()
    {
        var id1 = KnowledgeGraphService.GenerateEntityId("Alice", "PER");
        var id2 = KnowledgeGraphService.GenerateEntityId("Alice", "PER");

        id1.Should().Be(id2);
    }

    [Fact]
    public void GenerateEntityId_IsCaseInsensitive()
    {
        var id1 = KnowledgeGraphService.GenerateEntityId("Alice", "PER");
        var id2 = KnowledgeGraphService.GenerateEntityId("alice", "PER");
        var id3 = KnowledgeGraphService.GenerateEntityId("ALICE", "PER");

        id1.Should().Be(id2);
        id2.Should().Be(id3);
    }

    [Fact]
    public void GenerateEntityId_DiffersByType()
    {
        var perId = KnowledgeGraphService.GenerateEntityId("Apple", "PER");
        var orgId = KnowledgeGraphService.GenerateEntityId("Apple", "ORG");

        perId.Should().NotBe(orgId);
        perId.Should().StartWith("per_");
        orgId.Should().StartWith("org_");
    }

    [Fact]
    public void GenerateEntityId_TrimsWhitespace()
    {
        var id1 = KnowledgeGraphService.GenerateEntityId("  Alice  ", "PER");
        var id2 = KnowledgeGraphService.GenerateEntityId("Alice", "PER");

        id1.Should().Be(id2);
    }

    private static ContentItem CreateContentItem(string id, string title) => new()
    {
        Id = id,
        Source = "test",
        Title = title,
        Url = $"https://example.com/{id}",
        Content = $"Content for {title}"
    };

    private static float[] CreateRandomEmbedding()
    {
        var rng = new Random(42);
        var embedding = new float[384];
        for (var i = 0; i < 384; i++)
            embedding[i] = (float)(rng.NextDouble() * 2 - 1);
        return Normalize(embedding);
    }

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

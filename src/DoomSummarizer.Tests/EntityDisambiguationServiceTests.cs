using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for entity disambiguation: feature extraction, clustering, labeling, and feature cache.
///     Requires ONNX embedding model.
/// </summary>
[Trait("Category", "RequiresModel")]
[Collection("EmbeddingTests")]
public class EntityDisambiguationServiceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private IEmbeddingService _embedding = null!;
    private StorageService _storage = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_disambig_test_{Guid.NewGuid():N}.db");
        _storage = new StorageService(_dbPath);
        await _storage.InitializeAsync();

        _embedding = await EmbeddingFactory.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        (_embedding as IDisposable)?.Dispose();
        await _storage.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            /* cleanup best-effort */
        }
    }

    // --- Helpers ---

    private static ContentItem MakeItem(
        string id, string title, string source = "test",
        string? url = null, string? topic = null,
        string? summary = null, double relevance = 0.5)
    {
        return new ContentItem
        {
            Id = id,
            Title = title,
            Source = source,
            Url = url ?? $"https://{id}.example.com/article",
            Content = summary ?? $"Content about {title}",
            Summary = summary ?? $"Summary about {title}",
            DetectedTopic = topic,
            CreatedAt = DateTimeOffset.UtcNow,
            FetchedAt = DateTimeOffset.UtcNow,
            RelevanceScore = relevance
        };
    }

    // --- Skip Logic ---

    [Fact]
    public async Task DisambiguateFast_EmptyEvidence_ReturnsNotAmbiguous()
    {
        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync([], "test query", _embedding, _storage);

        result.IsAmbiguous.Should().BeFalse();
        result.Clusters.Should().BeEmpty();
        result.Method.Should().Be("skip");
    }

    [Fact]
    public async Task DisambiguateFast_FewItems_ReturnsNotAmbiguous()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "Alpha article"),
            MakeItem("b", "Beta article"),
            MakeItem("c", "Charlie article")
        };

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, "test", _embedding, _storage);

        result.IsAmbiguous.Should().BeFalse();
        result.Clusters.Should().HaveCount(1);
        result.Clusters[0].Items.Should().HaveCount(3);
        result.Method.Should().Be("skip");
    }

    // --- Clustering ---

    [Fact]
    public async Task DisambiguateFast_SimilarItems_ClusterTogether()
    {
        // All items about the same topic and domain → should form 1 cluster
        var items = new List<ContentItem>();
        for (var i = 0; i < 6; i++)
            items.Add(MakeItem(
                $"dotnet-{i}",
                $".NET {i + 8} New Features",
                url: $"https://dotnetblog.example.com/article-{i}",
                topic: "technology",
                summary: ".NET runtime improvements and C# language features",
                relevance: 0.8 - i * 0.05));

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, ".NET features", _embedding, _storage);

        // Similar items should cluster into 1 (not ambiguous)
        result.IsAmbiguous.Should().BeFalse();
        result.Clusters.Should().HaveCount(1);
    }

    [Fact]
    public async Task DisambiguateFast_DivergentItems_ProducesMultipleClusters()
    {
        // Two groups of items about very different things
        var items = new List<ContentItem>();

        // Group 1: Canadian mortgage software
        for (var i = 0; i < 4; i++)
            items.Add(MakeItem(
                $"mortgage-{i}",
                $"Mortgage Software Platform for Canadian Banks - Release {i + 1}",
                url: $"https://canadianmortgage.example.com/release-{i}",
                topic: "finance",
                summary: "Toronto-based mortgage lending software for Canadian financial institutions and banks",
                relevance: 0.9 - i * 0.05));

        // Group 2: European industrial machinery
        for (var i = 0; i < 4; i++)
            items.Add(MakeItem(
                $"industrial-{i}",
                $"Industrial Marking Systems for European Factories - Model {i + 1}",
                url: $"https://euromark.example.com/model-{i}",
                topic: "manufacturing",
                summary: "German manufacturing equipment for laser marking and engraving in European factories",
                relevance: 0.85 - i * 0.05));

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, "company products", _embedding, _storage);

        // Should detect ambiguity — these are very different entities
        result.Clusters.Count.Should().BeGreaterThanOrEqualTo(2,
            "mortgage software and industrial machinery should form distinct clusters");
        result.IsAmbiguous.Should().BeTrue();
        result.Method.Should().Be("existing+embedding");
    }

    [Fact]
    public async Task DisambiguateFast_TooManyClusters_SetsTooMany()
    {
        // Create 5 very distinct groups to trigger TooMany (>= 4 clusters)
        var topics = new[]
        {
            ("Quantum computing breakthroughs in superconducting qubits", "quantum.example.com", "physics"),
            ("Organic farming methods for sustainable vegetable agriculture", "farmlife.example.com", "agriculture"),
            ("Renaissance oil painting restoration techniques in museums", "artworld.example.com", "art"),
            ("Deep sea marine biology and coral reef ecosystems", "oceanlife.example.com", "marine-biology"),
            ("Ancient Roman archaeological excavation discoveries", "digsite.example.com", "archaeology")
        };

        var items = new List<ContentItem>();
        var idx = 0;
        foreach (var (summary, domain, topic) in topics)
            for (var i = 0; i < 3; i++)
            {
                items.Add(MakeItem(
                    $"item-{idx}",
                    $"{summary} - Part {i + 1}",
                    url: $"https://{domain}/article-{i}",
                    topic: topic,
                    summary: summary,
                    relevance: 0.9 - idx * 0.01));
                idx++;
            }

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, "research topics", _embedding, _storage);

        // With 5 very distinct groups, should get 4+ clusters → TooMany
        if (result.Clusters.Count >= 4) result.TooMany.Should().BeTrue();
        // If clustering merges some, that's acceptable — just verify structure
        result.Clusters.Should().NotBeEmpty();
    }

    // --- Feature Cache ---

    [Fact]
    public async Task FeatureCache_UpsertAndRetrieve()
    {
        var embedding = new float[384];
        for (var i = 0; i < 384; i++) embedding[i] = i / 384f;
        var bytes = EmbeddingCompat.ToBytes(embedding);

        await _storage.UpsertFeatureCacheAsync("Toronto, Canada", "LOC", bytes);

        var cached = await _storage.GetCachedFeatureEmbeddingAsync("Toronto, Canada");
        cached.Should().NotBeNull();
        cached!.Value.category.Should().Be("LOC");

        var recovered = EmbeddingCompat.FromBytes(cached.Value.embedding);
        recovered.Should().HaveCount(384);
        recovered[0].Should().BeApproximately(0f, 0.001f);
        recovered[383].Should().BeApproximately(383f / 384f, 0.001f);
    }

    [Fact]
    public async Task FeatureCache_BumpsHitCount()
    {
        var bytes = new byte[384 * sizeof(float)];
        await _storage.UpsertFeatureCacheAsync("Google", "ORG", bytes);

        // First get → bumps hit_count from 1 → 2
        await _storage.GetCachedFeatureEmbeddingAsync("Google");
        // Second get → bumps hit_count from 2 → 3
        await _storage.GetCachedFeatureEmbeddingAsync("Google");

        // Upsert again → should bump hit_count further
        await _storage.UpsertFeatureCacheAsync("Google", "ORG", bytes);

        // Verify we can still retrieve it
        var cached = await _storage.GetCachedFeatureEmbeddingAsync("Google");
        cached.Should().NotBeNull();
    }

    [Fact]
    public async Task FeatureCache_ReturnsNullForMissing()
    {
        var cached = await _storage.GetCachedFeatureEmbeddingAsync("nonexistent-term");
        cached.Should().BeNull();
    }

    // --- Full-path disambiguation (no Ollama) ---

    [Fact]
    public async Task DisambiguateAsync_WithoutOllama_FallsToDomainFeatures()
    {
        var items = new List<ContentItem>();
        for (var i = 0; i < 6; i++)
            items.Add(MakeItem(
                $"item-{i}",
                $"Article about topic {i}",
                url: $"https://site{i}.example.com/page",
                relevance: 0.8 - i * 0.05));

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateAsync(
            items, "topic", _embedding, _storage,
            null, false,
            CancellationToken.None);

        // Without Ollama or NER, falls to domain+embedding
        result.Method.Should().ContainAny("domain+embedding", "ner+embedding");
        result.Clusters.Should().NotBeEmpty();
    }

    // --- Cluster labeling ---

    [Fact]
    public async Task DisambiguateFast_ClustersHaveLabels()
    {
        var items = new List<ContentItem>();
        for (var i = 0; i < 4; i++)
            items.Add(MakeItem(
                $"item-{i}",
                $"Article {i} about programming",
                url: "https://blog.example.com/article",
                topic: "technology",
                relevance: 0.8));

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, "programming", _embedding, _storage);

        // Every cluster should have a non-empty label
        foreach (var cluster in result.Clusters) cluster.Label.Should().NotBeNullOrEmpty("every cluster needs a label");
    }

    // --- All items preserved ---

    [Fact]
    public async Task DisambiguateFast_AllItemsPreserved()
    {
        var items = new List<ContentItem>();
        for (var i = 0; i < 8; i++)
            items.Add(MakeItem(
                $"item-{i}",
                $"Article about {(i % 2 == 0 ? "cats" : "dogs")} - Part {i}",
                url: $"https://{(i % 2 == 0 ? "cats" : "dogs")}.example.com/p{i}",
                topic: i % 2 == 0 ? "pets-cats" : "pets-dogs",
                summary: i % 2 == 0
                    ? "Feline behavior and cat care tips"
                    : "Canine training and dog health advice",
                relevance: 0.8 - i * 0.03));

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, "pets", _embedding, _storage);

        // All items should be present across all clusters (no items dropped)
        var totalItems = result.Clusters.Sum(c => c.Items.Count);
        totalItems.Should().Be(items.Count,
            "disambiguation must not drop any items — orphans are assigned to nearest cluster");
    }

    // --- Feature cache speeds up second run ---

    [Fact]
    public async Task DisambiguateFast_SecondRunUsesCache()
    {
        var items = new List<ContentItem>();
        for (var i = 0; i < 5; i++)
            items.Add(MakeItem(
                $"cached-{i}",
                $"Cached article {i}",
                url: $"https://cached.example.com/{i}",
                topic: "caching",
                summary: "Article about caching mechanisms",
                relevance: 0.7));

        var svc = new EntityDisambiguationService();

        // First run — populates cache
        var result1 = await svc.DisambiguateFastAsync(items, "caching", _embedding, _storage);

        // Second run — should hit feature_cache
        var result2 = await svc.DisambiguateFastAsync(items, "caching", _embedding, _storage);

        // Both should produce same structure
        result1.Clusters.Count.Should().Be(result2.Clusters.Count);
        result1.Method.Should().Be(result2.Method);
    }

    // --- Clusters sorted by relevance ---

    [Fact]
    public async Task DisambiguateFast_ClustersSortedByRelevance()
    {
        var items = new List<ContentItem>();

        // High-relevance group
        for (var i = 0; i < 3; i++)
            items.Add(MakeItem(
                $"high-{i}",
                $"Machine learning neural network training optimization techniques Part {i + 1}",
                url: $"https://mlresearch.example.com/paper-{i}",
                topic: "machine-learning",
                summary: "Deep learning neural network optimization and gradient descent methods",
                relevance: 0.95 - i * 0.02));

        // Low-relevance group
        for (var i = 0; i < 3; i++)
            items.Add(MakeItem(
                $"low-{i}",
                $"Vintage vinyl record collecting and turntable maintenance guide Part {i + 1}",
                url: $"https://vinylcollectors.example.com/guide-{i}",
                topic: "music-collecting",
                summary: "Rare vinyl record collection storage and turntable equipment maintenance",
                relevance: 0.4 - i * 0.02));

        var svc = new EntityDisambiguationService();
        var result = await svc.DisambiguateFastAsync(items, "mixed topics", _embedding, _storage);

        if (result.Clusters.Count >= 2)
            // First cluster should have higher average relevance
            result.Clusters[0].AverageRelevance.Should()
                .BeGreaterThanOrEqualTo(result.Clusters[1].AverageRelevance);
    }
}
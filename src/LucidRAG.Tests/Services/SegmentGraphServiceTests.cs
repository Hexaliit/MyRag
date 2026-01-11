using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using Mostlylucid.DocSummarizer.Models;

namespace LucidRAG.Tests.Services;

/// <summary>
/// Unit tests for SegmentGraphService.
/// Tests segment graph building, traversal, and expansion.
/// Requires PostgreSQL database - tests are skipped if database unavailable.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class SegmentGraphServiceTests : IDisposable
{
    private readonly RagDocumentsDbContext? _db;
    private readonly SegmentGraphService? _service;
    private readonly Guid _testDocumentId = Guid.NewGuid();
    private readonly bool _dbAvailable;

    public SegmentGraphServiceTests()
    {
        try
        {
            _db = TestDbContextFactory.CreateInMemory();
            _service = new SegmentGraphService(_db, NullLogger<SegmentGraphService>.Instance);
            _dbAvailable = true;
        }
        catch
        {
            // Database not available (pgvector dependencies) - tests will skip
            _dbAvailable = false;
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool SkipIfNoDb()
    {
        return !_dbAvailable;
    }

    #region BuildGraphAsync Tests

    [Fact]
    public async Task BuildGraphAsync_WithSingleSegment_ReturnsZeroLinks()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        var segments = new List<Segment>
        {
            CreateSegment("hash1", "First segment content", 0)
        };

        // Act
        var linkCount = await _service!.BuildGraphAsync(_testDocumentId, segments);

        // Assert
        linkCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildGraphAsync_WithTwoSegments_CreatesSequentialLink()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        var segments = new List<Segment>
        {
            CreateSegment("hash1", "First segment content", 0),
            CreateSegment("hash2", "Second segment content", 1)
        };

        // Act
        var linkCount = await _service!.BuildGraphAsync(_testDocumentId, segments);

        // Assert
        linkCount.Should().BeGreaterThan(0);
        var links = _db!.Set<SegmentLink>().Where(l => l.DocumentId == _testDocumentId).ToList();
        links.Should().Contain(l => l.LinkType == SegmentLinkTypes.Sequential);
    }

    [Fact]
    public async Task BuildGraphAsync_WithSameHeading_CreatesHeadingLinks()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        var segments = new List<Segment>
        {
            CreateSegment("hash1", "First content", 0, "Introduction"),
            CreateSegment("hash2", "Second content", 1, "Introduction"),
            CreateSegment("hash3", "Third content", 2, "Introduction")
        };

        // Act
        await _service!.BuildGraphAsync(_testDocumentId, segments);

        // Assert
        var links = _db!.Set<SegmentLink>()
            .Where(l => l.DocumentId == _testDocumentId && l.LinkType == SegmentLinkTypes.Heading)
            .ToList();
        links.Should().NotBeEmpty();
    }

    [Fact]
    public async Task BuildGraphAsync_WithHighSemanticSimilarity_CreatesSemanticLinks()
    {
        if (SkipIfNoDb()) return;

        // Arrange - Create segments with nearly identical embeddings
        var embedding1 = CreateNormalizedEmbedding(0.9f, 0.1f, 0.1f);
        var embedding2 = CreateNormalizedEmbedding(0.88f, 0.12f, 0.12f); // Very similar

        var segments = new List<Segment>
        {
            CreateSegment("hash1", "First content", 0, embedding: embedding1),
            CreateSegment("hash2", "Second content", 1, embedding: embedding2)
        };

        // Act
        await _service!.BuildGraphAsync(_testDocumentId, segments);

        // Assert
        var links = _db!.Set<SegmentLink>()
            .Where(l => l.DocumentId == _testDocumentId && l.LinkType == SegmentLinkTypes.Semantic)
            .ToList();
        links.Should().NotBeEmpty("High semantic similarity should create semantic links");
    }

    [Fact]
    public async Task BuildGraphAsync_ClearsExistingLinksFirst()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        var segments1 = new List<Segment>
        {
            CreateSegment("hash1", "First", 0),
            CreateSegment("hash2", "Second", 1)
        };

        var segments2 = new List<Segment>
        {
            CreateSegment("hash3", "Third", 0),
            CreateSegment("hash4", "Fourth", 1)
        };

        // Act
        await _service!.BuildGraphAsync(_testDocumentId, segments1);
        var linksAfterFirst = _db!.Set<SegmentLink>()
            .Where(l => l.DocumentId == _testDocumentId)
            .ToList();

        await _service.BuildGraphAsync(_testDocumentId, segments2);
        var linksAfterSecond = _db.Set<SegmentLink>()
            .Where(l => l.DocumentId == _testDocumentId)
            .ToList();

        // Assert
        linksAfterSecond.Should().NotContain(l =>
            l.SourceSegmentHash == "hash1" || l.TargetSegmentHash == "hash1");
    }

    #endregion

    #region GetConnectedSegmentsAsync Tests

    [Fact]
    public async Task GetConnectedSegmentsAsync_ReturnsConnectedSegments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksAsync();

        // Act
        var connected = await _service!.GetConnectedSegmentsAsync("hash1");

        // Assert
        connected.Should().NotBeEmpty();
        connected.Should().Contain(c => c.SegmentHash == "hash2");
    }

    [Fact]
    public async Task GetConnectedSegmentsAsync_WithLinkTypeFilter_ReturnsOnlyMatchingTypes()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksAsync();

        // Act
        var connected = await _service!.GetConnectedSegmentsAsync(
            "hash1",
            linkTypes: [SegmentLinkTypes.Sequential]);

        // Assert
        connected.Should().OnlyContain(c => c.LinkType == SegmentLinkTypes.Sequential);
    }

    [Fact]
    public async Task GetConnectedSegmentsAsync_RespectsMaxResults()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedManyLinksAsync("source_hash", 20);

        // Act
        var connected = await _service!.GetConnectedSegmentsAsync("source_hash", maxResults: 5);

        // Assert
        connected.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetConnectedSegmentsAsync_OrdersByWeight()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksWithDifferentWeightsAsync();

        // Act
        var connected = await _service!.GetConnectedSegmentsAsync("weighted_source");

        // Assert
        var weights = connected.Select(c => c.Weight).ToList();
        weights.Should().BeInDescendingOrder();
    }

    #endregion

    #region ExpandRetrievalAsync Tests

    [Fact]
    public async Task ExpandRetrievalAsync_ExpandsWithConnectedSegments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksAsync();
        var initialHashes = new[] { "hash1" };

        // Act
        var expanded = await _service!.ExpandRetrievalAsync(initialHashes, expansionDepth: 1);

        // Assert
        expanded.Should().NotBeEmpty();
        expanded.Should().NotContain("hash1"); // Should not include original
    }

    [Fact]
    public async Task ExpandRetrievalAsync_RespectsMinWeight()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksWithDifferentWeightsAsync();
        var initialHashes = new[] { "weighted_source" };

        // Act
        var expanded = await _service!.ExpandRetrievalAsync(
            initialHashes,
            minWeight: 0.8);

        // Assert
        // Should only include high-weight connections
        expanded.Should().OnlyContain(h => h.Contains("high"));
    }

    [Fact]
    public async Task ExpandRetrievalAsync_WithMultipleDepths_ExpandsFurther()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedChainedLinksAsync();
        var initialHashes = new[] { "chain_start" };

        // Act
        var depth1 = await _service!.ExpandRetrievalAsync(initialHashes, expansionDepth: 1);
        var depth2 = await _service.ExpandRetrievalAsync(initialHashes, expansionDepth: 2);

        // Assert
        depth2.Count.Should().BeGreaterThanOrEqualTo(depth1.Count);
    }

    #endregion

    #region CalculateGraphSalienceAsync Tests

    [Fact]
    public async Task CalculateGraphSalienceAsync_ReturnsNormalizedScores()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksAsync();

        // Act
        var salience = await _service!.CalculateGraphSalienceAsync(_testDocumentId);

        // Assert
        salience.Should().NotBeEmpty();
        salience.Values.Should().OnlyContain(v => v >= 0 && v <= 1);
    }

    [Fact]
    public async Task CalculateGraphSalienceAsync_HighlyConnectedNodesHaveHigherSalience()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedHubAndSpokeGraphAsync();

        // Act
        var salience = await _service!.CalculateGraphSalienceAsync(_testDocumentId);

        // Assert
        salience.Should().ContainKey("hub_node");
        salience["hub_node"].Should().BeGreaterThan(salience.Values.Average());
    }

    #endregion

    #region GetStatsAsync Tests

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectStatistics()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedLinksAsync();

        // Act
        var stats = await _service!.GetStatsAsync(_testDocumentId);

        // Assert
        stats.TotalLinks.Should().BeGreaterThan(0);
        stats.UniqueSegments.Should().BeGreaterThan(0);
        stats.AverageWeight.Should().BeGreaterThan(0);
        stats.TypeDistribution.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_WithNoLinks_ReturnsZeroStats()
    {
        if (SkipIfNoDb()) return;

        // Act
        var stats = await _service!.GetStatsAsync(Guid.NewGuid());

        // Assert
        stats.TotalLinks.Should().Be(0);
        stats.UniqueSegments.Should().Be(0);
        stats.AverageWeight.Should().Be(0);
    }

    #endregion

    #region Helper Methods

    private static Segment CreateSegment(
        string hash,
        string content,
        int index,
        string? headingPath = null,
        float[]? embedding = null)
    {
        // Use the proper Segment constructor with content hash override
        var segment = new Segment(
            docId: "test_doc",
            text: content,
            type: SegmentType.Sentence,
            index: index,
            startChar: 0,
            endChar: content.Length,
            contentHashOverride: hash)
        {
            HeadingPath = headingPath ?? "",
            Embedding = embedding
        };
        return segment;
    }

    private static float[] CreateNormalizedEmbedding(params float[] values)
    {
        // Extend to 384 dimensions with zeros
        var embedding = new float[384];
        Array.Copy(values, embedding, Math.Min(values.Length, 384));
        // Normalize
        var magnitude = MathF.Sqrt(embedding.Sum(x => x * x));
        if (magnitude > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
                embedding[i] /= magnitude;
        }
        return embedding;
    }

    private async Task SeedLinksAsync()
    {
        var links = new[]
        {
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "hash1",
                TargetSegmentHash = "hash2",
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 0.7
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "hash2",
                TargetSegmentHash = "hash3",
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 0.7
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "hash1",
                TargetSegmentHash = "hash3",
                LinkType = SegmentLinkTypes.Heading,
                Weight = 0.8
            }
        };

        _db!.Set<SegmentLink>().AddRange(links);
        await _db.SaveChangesAsync();
    }

    private async Task SeedManyLinksAsync(string sourceHash, int count)
    {
        var links = Enumerable.Range(1, count).Select(i => new SegmentLink
        {
            Id = Guid.NewGuid(),
            DocumentId = _testDocumentId,
            SourceSegmentHash = sourceHash,
            TargetSegmentHash = $"target_{i}",
            LinkType = SegmentLinkTypes.Sequential,
            Weight = 0.5 + (i * 0.01)
        });

        _db!.Set<SegmentLink>().AddRange(links);
        await _db.SaveChangesAsync();
    }

    private async Task SeedLinksWithDifferentWeightsAsync()
    {
        var links = new[]
        {
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "weighted_source",
                TargetSegmentHash = "high_weight_1",
                LinkType = SegmentLinkTypes.Semantic,
                Weight = 0.95
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "weighted_source",
                TargetSegmentHash = "high_weight_2",
                LinkType = SegmentLinkTypes.Semantic,
                Weight = 0.85
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "weighted_source",
                TargetSegmentHash = "low_weight_1",
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 0.3
            }
        };

        _db!.Set<SegmentLink>().AddRange(links);
        await _db.SaveChangesAsync();
    }

    private async Task SeedChainedLinksAsync()
    {
        var links = new[]
        {
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "chain_start",
                TargetSegmentHash = "chain_middle",
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 0.7
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "chain_middle",
                TargetSegmentHash = "chain_end",
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 0.7
            }
        };

        _db!.Set<SegmentLink>().AddRange(links);
        await _db.SaveChangesAsync();
    }

    private async Task SeedHubAndSpokeGraphAsync()
    {
        var links = Enumerable.Range(1, 10).Select(i => new SegmentLink
        {
            Id = Guid.NewGuid(),
            DocumentId = _testDocumentId,
            SourceSegmentHash = "hub_node",
            TargetSegmentHash = $"spoke_{i}",
            LinkType = SegmentLinkTypes.Semantic,
            Weight = 0.8
        }).ToList();

        // Add some spoke-to-spoke connections
        links.Add(new SegmentLink
        {
            Id = Guid.NewGuid(),
            DocumentId = _testDocumentId,
            SourceSegmentHash = "spoke_1",
            TargetSegmentHash = "spoke_2",
            LinkType = SegmentLinkTypes.Sequential,
            Weight = 0.6
        });

        _db!.Set<SegmentLink>().AddRange(links);
        await _db.SaveChangesAsync();
    }

    #endregion
}

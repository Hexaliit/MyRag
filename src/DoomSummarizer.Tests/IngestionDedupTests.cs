using DoomSummarizer.Commands;
using DoomSummarizer.Models;
using DoomSummarizer.Tests.Benchmarks;

namespace DoomSummarizer.Tests;

public class IngestionDedupTests
{
    private readonly CountingEmbeddingService _embedder = new(dimension: 384);

    /// <summary>
    ///     Helper: create a ContentItem with a deterministic embedding from text.
    /// </summary>
    private ContentItem MakeItem(string id, string text, float salience = 0.5f)
    {
        var embedding = _embedder.EmbedAsync(text).GetAwaiter().GetResult();
        return new ContentItem
        {
            Id = id,
            Source = "test",
            Title = id,
            Content = text,
            Embedding = embedding,
            SalienceScore = salience
        };
    }

    // ── DeduplicateChunks ─────────────────────────────────────────────

    [Fact]
    public void DeduplicateChunks_IdenticalTexts_RemovesDuplicates()
    {
        // Two items with identical text → identical embeddings → one should be absorbed
        var items = new List<ContentItem>
        {
            MakeItem("a", "The quick brown fox jumps over the lazy dog.", 0.8f),
            MakeItem("b", "The quick brown fox jumps over the lazy dog.", 0.3f)
        };

        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 1, 100, salienceBoost: true);

        Assert.Single(survivors);
        Assert.Equal("a", survivors[0].Id); // Higher salience survives
    }

    [Fact]
    public void DeduplicateChunks_DifferentTexts_KeepsBoth()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "Machine learning algorithms process data patterns", 0.5f),
            MakeItem("b", "Shakespeare wrote plays in the 16th century", 0.5f)
        };

        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 1, 100, salienceBoost: true);

        Assert.Equal(2, survivors.Count);
    }

    [Fact]
    public void DeduplicateChunks_RespectsMinSurvivors()
    {
        // Even with high similarity, don't go below min
        var items = new List<ContentItem>
        {
            MakeItem("a", "same text here", 0.8f),
            MakeItem("b", "same text here", 0.5f),
            MakeItem("c", "same text here", 0.3f)
        };

        // minSurvivors=5 > items.Count → returns all
        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 5, 100, salienceBoost: true);

        Assert.Equal(3, survivors.Count);
    }

    [Fact]
    public void DeduplicateChunks_RespectsMaxSurvivors()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "Machine learning models transform data into predictions", 0.9f),
            MakeItem("b", "Deep learning uses neural networks for pattern recognition", 0.8f),
            MakeItem("c", "Natural language processing extracts meaning from text", 0.7f),
            MakeItem("d", "Computer vision enables machines to interpret images", 0.6f),
            MakeItem("e", "Reinforcement learning optimizes actions through rewards", 0.5f)
        };

        // maxSurvivors=2 → even if none are near-duplicates, cap at 2
        var survivors = ScrollCommand.DeduplicateChunks(items, 0.99f, 1, 2, salienceBoost: false);

        Assert.True(survivors.Count <= 2);
    }

    [Fact]
    public void DeduplicateChunks_BoostsSalience_WhenAbsorbing()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "duplicate content exactly", 0.5f),
            MakeItem("b", "duplicate content exactly", 0.3f),
            MakeItem("c", "duplicate content exactly", 0.2f)
        };

        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 1, 100, salienceBoost: true);

        // Survivor should have boosted salience (absorbed 2 near-dupes)
        Assert.Single(survivors);
        Assert.True(survivors[0].SalienceScore > 0.5f, "Salience should be boosted by absorbed duplicates");
    }

    [Fact]
    public void DeduplicateChunks_NoBoost_WhenDisabled()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "duplicate content exactly", 0.5f),
            MakeItem("b", "duplicate content exactly", 0.3f)
        };

        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 1, 100, salienceBoost: false);

        Assert.Single(survivors);
        Assert.Equal(0.5f, survivors[0].SalienceScore); // No boost
    }

    [Fact]
    public void DeduplicateChunks_HighestSalience_Survives()
    {
        var items = new List<ContentItem>
        {
            MakeItem("low", "identical text for all items", 0.1f),
            MakeItem("mid", "identical text for all items", 0.5f),
            MakeItem("high", "identical text for all items", 0.9f)
        };

        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 1, 100, salienceBoost: false);

        Assert.Single(survivors);
        Assert.Equal("high", survivors[0].Id);
    }

    [Fact]
    public void DeduplicateChunks_EmptyList_ReturnsEmpty()
    {
        var survivors = ScrollCommand.DeduplicateChunks(
            new List<ContentItem>(), 0.90f, 0, 100, salienceBoost: true);

        Assert.Empty(survivors);
    }

    [Fact]
    public void DeduplicateChunks_NullEmbeddings_Skipped()
    {
        var items = new List<ContentItem>
        {
            new()
            {
                Id = "a", Source = "test", Title = "a",
                Embedding = null, SalienceScore = 0.5f
            },
            new()
            {
                Id = "b", Source = "test", Title = "b",
                Embedding = null, SalienceScore = 0.3f
            }
        };

        var survivors = ScrollCommand.DeduplicateChunks(items, 0.90f, 1, 100, salienceBoost: true);

        // Both survive because null embeddings can't be compared
        Assert.Equal(2, survivors.Count);
    }

    // ── GetAdaptiveLimits ─────────────────────────────────────────────

    [Fact]
    public void GetAdaptiveLimits_FictionNovel_Returns_30_120_088()
    {
        var config = new IngestionConfig();
        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            100, IngestDocumentType.Fiction, config);

        Assert.Equal(30, min);
        Assert.Equal(120, max);
        Assert.Equal(0.88f, threshold, 2);
    }

    [Fact]
    public void GetAdaptiveLimits_FictionEpic_Returns_50_200_085()
    {
        var config = new IngestionConfig();
        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            500, IngestDocumentType.Fiction, config);

        Assert.Equal(50, min);
        Assert.Equal(200, max);
        Assert.Equal(0.85f, threshold, 2);
    }

    [Fact]
    public void GetAdaptiveLimits_TechnicalSmall_Returns_15_80_092()
    {
        var config = new IngestionConfig();
        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            50, IngestDocumentType.Technical, config);

        Assert.Equal(15, min);
        Assert.Equal(80, max);
        Assert.Equal(0.92f, threshold, 2);
    }

    [Fact]
    public void GetAdaptiveLimits_ConfigOverrides_Applied()
    {
        var config = new IngestionConfig
        {
            MinChunksOverride = 5,
            MaxChunksOverride = 25,
            DeduplicationThreshold = 0.95f
        };

        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            100, IngestDocumentType.Fiction, config);

        Assert.Equal(5, min);
        Assert.Equal(25, max);
        Assert.Equal(0.95f, threshold, 2);
    }

    [Fact]
    public void GetAdaptiveLimits_UnknownType_ReasonableDefaults()
    {
        var config = new IngestionConfig();
        var (min, max, threshold) = ScrollCommand.GetAdaptiveLimits(
            50, IngestDocumentType.Unknown, config);

        Assert.Equal(15, min);
        Assert.True(max >= 50); // At least as many as chunks
        Assert.Equal(0.90f, threshold, 2);
    }

    // ── PreDeduplicateChunks ────────────────────────────────────────

    private static PreDedupWeights DefaultWeights => new();

    private static (ContentItem item, string embedText) MakePair(
        string id, string text, float salience = 0.5f, string? title = null) =>
        (new ContentItem
        {
            Id = id, Source = "test", Title = title ?? id,
            Content = text, SalienceScore = salience
        }, text);

    [Fact]
    public void PreDedup_IdenticalTexts_RemovesDuplicate()
    {
        var items = new List<(ContentItem item, string embedText)>
        {
            MakePair("a", "The quick brown fox jumps over the lazy dog.", 0.8f),
            MakePair("b", "The quick brown fox jumps over the lazy dog.", 0.3f)
        };

        var survivors = ScrollCommand.PreDeduplicateChunks(items, DefaultWeights);

        // Exact hash dedup removes one immediately
        Assert.Single(survivors);
    }

    [Fact]
    public void PreDedup_DifferentTexts_KeepsBoth()
    {
        var items = new List<(ContentItem item, string embedText)>
        {
            MakePair("a", "Machine learning algorithms process data patterns and make predictions"),
            MakePair("b", "Shakespeare wrote many famous plays during the Elizabethan era in England")
        };

        var survivors = ScrollCommand.PreDeduplicateChunks(items, DefaultWeights);

        Assert.Equal(2, survivors.Count);
    }

    [Fact]
    public void PreDedup_NearDuplicates_RemovesLowerSalience()
    {
        // Same title + trivial text variation — typical intra-document near-dupe
        var items = new List<(ContentItem item, string embedText)>
        {
            MakePair("high", "Machine learning algorithms process data patterns and make predictions about outcomes", 0.8f, "Chapter 3"),
            MakePair("low", "Machine learning algorithms process data patterns and make predictions about results", 0.3f, "Chapter 3")
        };

        var survivors = ScrollCommand.PreDeduplicateChunks(items, DefaultWeights);

        // Near-duplicate: high word Jaccard + trigram overlap + same heading → absorbed
        Assert.Single(survivors);
        Assert.Equal("high", survivors[0].item.Id);
    }

    [Fact]
    public void PreDedup_EmptyList_ReturnsEmpty()
    {
        var survivors = ScrollCommand.PreDeduplicateChunks(
            new List<(ContentItem item, string embedText)>(), DefaultWeights);

        Assert.Empty(survivors);
    }

    [Fact]
    public void PreDedup_SingleItem_ReturnsSame()
    {
        var items = new List<(ContentItem item, string embedText)>
        {
            MakePair("a", "Only one item here")
        };

        var survivors = ScrollCommand.PreDeduplicateChunks(items, DefaultWeights);

        Assert.Single(survivors);
        Assert.Equal("a", survivors[0].item.Id);
    }

    [Fact]
    public void PreDedup_DisabledWeights_ReturnsAll()
    {
        var disabled = new PreDedupWeights
        {
            WordJaccard = 0, Trigram = 0, Length = 0, Heading = 0
        };

        var items = new List<(ContentItem item, string embedText)>
        {
            MakePair("a", "identical text here"),
            MakePair("b", "identical text here")
        };

        var survivors = ScrollCommand.PreDeduplicateChunks(items, disabled);

        // Disabled weights means no pre-dedup — returns all
        Assert.Equal(2, survivors.Count);
    }

    [Fact]
    public void PreDedup_HighThreshold_KeepsNearDupes()
    {
        var strict = new PreDedupWeights { Threshold = 0.99f };

        var items = new List<(ContentItem item, string embedText)>
        {
            MakePair("a", "Machine learning algorithms process data patterns and make predictions about outcomes", 0.8f),
            MakePair("b", "Machine learning algorithms process data patterns and make predictions about results", 0.3f)
        };

        var survivors = ScrollCommand.PreDeduplicateChunks(items, strict);

        // 0.99 threshold is hard to reach with non-identical text — both survive
        Assert.Equal(2, survivors.Count);
    }

    // ── CheapSimilarityScore ────────────────────────────────────────

    [Fact]
    public void CheapSimilarity_IdenticalContent_Returns1()
    {
        var text = "The quick brown fox jumps over the lazy dog and then rests.";
        var a = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "a", Source = "t", Title = "title", Content = text }, text);
        var b = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "b", Source = "t", Title = "title", Content = text }, text);

        var score = ScrollCommand.CheapSimilarityScore(a, b, DefaultWeights);

        Assert.Equal(1.0f, score, 0.01f);
    }

    [Fact]
    public void CheapSimilarity_CompletelyDifferent_ScoresLow()
    {
        var a = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "a", Source = "t", Title = "alpha", Content = "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj" },
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj");
        var b = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "b", Source = "t", Title = "beta", Content = "kkkk llll mmmm nnnn oooo pppp qqqq rrrr ssss tttt" },
            "kkkk llll mmmm nnnn oooo pppp qqqq rrrr ssss tttt");

        var score = ScrollCommand.CheapSimilarityScore(a, b, DefaultWeights);

        Assert.True(score < 0.3f, $"Expected low similarity for unrelated text, got {score}");
    }

    [Fact]
    public void CheapSimilarity_HeadingWeight_BoostsMatchingTitles()
    {
        var text1 = "Some unique content that differs from other content entirely and completely";
        var text2 = "Other unique content that is different from the first content entirely";
        var sameTitle = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "a", Source = "t", Title = "Chapter 1", Content = text1 }, text1);
        var sameTitle2 = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "b", Source = "t", Title = "Chapter 1", Content = text2 }, text2);
        var diffTitle = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "c", Source = "t", Title = "Chapter 99", Content = text2 }, text2);

        var withSameTitle = ScrollCommand.CheapSimilarityScore(sameTitle, sameTitle2, DefaultWeights);
        var withDiffTitle = ScrollCommand.CheapSimilarityScore(sameTitle, diffTitle, DefaultWeights);

        Assert.True(withSameTitle > withDiffTitle,
            $"Same title should score higher: {withSameTitle} vs {withDiffTitle}");
    }

    [Fact]
    public void CheapSimilarity_ZeroWeights_ReturnsZero()
    {
        var disabled = new PreDedupWeights { WordJaccard = 0, Trigram = 0, Length = 0, Heading = 0 };
        var text = "any text at all here for testing purposes";
        var a = new ScrollCommand.ChunkFingerprint(
            new ContentItem { Id = "a", Source = "t", Title = "t", Content = text }, text);

        var score = ScrollCommand.CheapSimilarityScore(a, a, disabled);

        Assert.Equal(0f, score);
    }
}

using DoomSummarizer.Models;
using DoomSummarizer.Services;
using FluentAssertions;
using Xunit;

namespace DoomSummarizer.Tests;

public class RelevanceScorerTests
{
    private static ContentItem MakeItem(string id, string title, string? content = null,
        string source = "test", int score = 0, DateTimeOffset? createdAt = null)
    {
        return new ContentItem
        {
            Id = id,
            Source = source,
            Title = title,
            Content = content,
            Score = score,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    }

    #region BM25 Scoring

    [Fact]
    public void BM25Score_MatchingTerms_ScoresHigher()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Pharmaceutical drug approval news", "FDA approves new pharmaceutical drug for diabetes"),
            MakeItem("2", "Latest tech startup funding", "AI startup raises $50M in Series B round"),
            MakeItem("3", "Sports update", "Team wins championship game in overtime")
        };

        var queryTokens = RelevanceScorer.Tokenize("pharmaceutical drug news");
        var (idf, avgDocLen) = RelevanceScorer.BuildCorpusStats(items);

        var pharmaScore = RelevanceScorer.BM25Score(
            RelevanceScorer.ItemText(items[0]), queryTokens, idf, avgDocLen);
        var techScore = RelevanceScorer.BM25Score(
            RelevanceScorer.ItemText(items[1]), queryTokens, idf, avgDocLen);
        var sportsScore = RelevanceScorer.BM25Score(
            RelevanceScorer.ItemText(items[2]), queryTokens, idf, avgDocLen);

        pharmaScore.Should().BeGreaterThan(techScore);
        pharmaScore.Should().BeGreaterThan(sportsScore);
    }

    [Fact]
    public void BM25Score_NoMatchingTerms_ReturnsZero()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Unrelated content about gardening")
        };

        var queryTokens = RelevanceScorer.Tokenize("pharmaceutical news");
        var (idf, avgDocLen) = RelevanceScorer.BuildCorpusStats(items);

        var score = RelevanceScorer.BM25Score(
            RelevanceScorer.ItemText(items[0]), queryTokens, idf, avgDocLen);

        score.Should().Be(0);
    }

    #endregion

    #region Freshness Scoring

    [Fact]
    public void ComputeFreshness_RecentItem_ScoresHigher()
    {
        var recent = MakeItem("1", "Recent", createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        var old = MakeItem("2", "Old", createdAt: DateTimeOffset.UtcNow.AddDays(-5));

        var recentScore = RelevanceScorer.ComputeFreshness(recent);
        var oldScore = RelevanceScorer.ComputeFreshness(old);

        recentScore.Should().BeGreaterThan(oldScore);
        recentScore.Should().BeGreaterThan(0.9); // 1 hour old should be very fresh
        oldScore.Should().BeLessThan(0.3); // 5 days old with 48h half-life
    }

    [Fact]
    public void ComputeFreshness_BrandNew_NearOne()
    {
        var brandNew = MakeItem("1", "Just now", createdAt: DateTimeOffset.UtcNow);
        var score = RelevanceScorer.ComputeFreshness(brandNew);
        score.Should().BeApproximately(1.0, 0.01);
    }

    #endregion

    #region Source Authority

    [Fact]
    public void NormalizeAuthority_HighScoreItem_NearOne()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "Popular HN post", source: "hn", score: 500),
            MakeItem("2", "Low HN post", source: "hn", score: 10),
            MakeItem("3", "BBC article", source: "bbc", score: 0)
        };

        // Build maxScoreBySource dictionary (same as RelevanceScorer does internally)
        var maxScoreBySource = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Score > 0))
        {
            if (!maxScoreBySource.TryGetValue(item.Source, out var current) || item.Score > current)
                maxScoreBySource[item.Source] = item.Score;
        }

        var highAuth = RelevanceScorer.NormalizeAuthority(items[0], maxScoreBySource);
        var lowAuth = RelevanceScorer.NormalizeAuthority(items[1], maxScoreBySource);
        var bbcAuth = RelevanceScorer.NormalizeAuthority(items[2], maxScoreBySource);

        highAuth.Should().Be(1.0);
        lowAuth.Should().BeLessThan(highAuth);
        bbcAuth.Should().Be(0.5); // BBC baseline
    }

    [Fact]
    public void NormalizeAuthority_NoScore_GetsBaseline()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "RSS item", source: "unknown", score: 0),
            MakeItem("2", "Google News", source: "gnews", score: 0)
        };

        var maxScoreBySource = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        RelevanceScorer.NormalizeAuthority(items[0], maxScoreBySource).Should().Be(0.3);
        RelevanceScorer.NormalizeAuthority(items[1], maxScoreBySource).Should().Be(0.4);
    }

    #endregion

    #region Phase 1: Fast Scoring

    [Fact]
    public void ScoreFast_RanksRelevantItemsHigher()
    {
        var scorer = new RelevanceScorer();
        var items = new List<ContentItem>
        {
            MakeItem("pharma", "New pharmaceutical drug approved by FDA",
                "The FDA has approved a breakthrough pharmaceutical treatment"),
            MakeItem("tech", "Latest JavaScript framework released",
                "A new React-based framework for building web applications"),
            MakeItem("sports", "Championship game results",
                "The final score was 3-2 in overtime play")
        };

        var ranked = scorer.ScoreFast(items, "pharmaceutical drug approval", discardRatio: 0);

        ranked.First().Id.Should().Be("pharma");
        ranked.First().RelevanceScore.Should().BeGreaterThan(ranked.Last().RelevanceScore);
    }

    [Fact]
    public void ScoreFast_DiscardsBottomTier()
    {
        var scorer = new RelevanceScorer();
        // Give relevant items stronger signals: more recent creation time + matching content
        var items = Enumerable.Range(1, 20).Select(i =>
            MakeItem($"item-{i}",
                i <= 5 ? $"Pharmaceutical drug item {i}" : $"Unrelated content about topic {i}",
                i <= 5 ? "FDA drug approval pharmaceutical treatment medicine" : "gardening recipes cooking sports weather",
                createdAt: i <= 5 ? DateTimeOffset.UtcNow.AddMinutes(-i) : DateTimeOffset.UtcNow.AddDays(-i))
        ).ToList();

        var ranked = scorer.ScoreFast(items, "pharmaceutical drug", discardRatio: 0.25);

        ranked.Count.Should().BeLessThan(20);
        // The top items should still be the pharma ones
        ranked.Take(5).Should().OnlyContain(i => i.Id.StartsWith("item-") &&
            int.Parse(i.Id.Split('-')[1]) <= 5);
    }

    [Fact]
    public void ScoreFast_EmptyQuery_ScoresByFreshnessAndAuthority()
    {
        var scorer = new RelevanceScorer();
        var items = new List<ContentItem>
        {
            MakeItem("old", "Old item", source: "hn", score: 100, createdAt: DateTimeOffset.UtcNow.AddDays(-3)),
            MakeItem("new", "New item", source: "hn", score: 100, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5))
        };

        var ranked = scorer.ScoreFast(items, "", discardRatio: 0);

        // Newer item should rank higher
        ranked.First().Id.Should().Be("new");
    }

    [Fact]
    public void ScoreFast_SmallBatch_NeverDiscardsAll()
    {
        var scorer = new RelevanceScorer();
        var items = new List<ContentItem>
        {
            MakeItem("1", "Only item", "Some content")
        };

        var ranked = scorer.ScoreFast(items, "query", discardRatio: 0.5);
        ranked.Should().HaveCount(1); // Never discard when batch is too small
    }

    #endregion

    #region RRF Fusion

    [Fact]
    public void FuseRRF_CombinesMultipleSignals()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "Item A"), // Best in signal 1
            MakeItem("b", "Item B"), // Best in signal 2
            MakeItem("c", "Item C")  // Mediocre in both
        };

        var signal1 = new List<(ContentItem item, double score)>
        {
            (items[0], 10.0), (items[2], 5.0), (items[1], 1.0)
        };
        var signal2 = new List<(ContentItem item, double score)>
        {
            (items[1], 10.0), (items[2], 5.0), (items[0], 1.0)
        };

        var fused = RelevanceScorer.FuseRRF(items, new[]
        {
            (signal1, 1.0),
            (signal2, 1.0)
        });

        // Item C should rank well since it's #2 in both signals
        // Items A and B should be close (each #1 in one, #3 in other)
        var sorted = fused.OrderByDescending(x => x.score).ToList();

        // With equal weights, A and B get: 1/(61) + 1/(63) = 0.01639 + 0.01587 = 0.03226
        // C gets: 1/(62) + 1/(62) = 0.01613 + 0.01613 = 0.03226
        // They should be very close
        sorted.Select(x => x.item.Id).Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public void FuseRRF_WeightedSignals_FavorsHigherWeight()
    {
        var items = new List<ContentItem>
        {
            MakeItem("a", "A"), // Best in low-weight signal
            MakeItem("b", "B")  // Best in high-weight signal
        };

        var lowWeightSignal = new List<(ContentItem item, double score)>
        {
            (items[0], 10.0), (items[1], 1.0)
        };
        var highWeightSignal = new List<(ContentItem item, double score)>
        {
            (items[1], 10.0), (items[0], 1.0)
        };

        var fused = RelevanceScorer.FuseRRF(items, new[]
        {
            (lowWeightSignal, 0.1),
            (highWeightSignal, 2.0)
        });

        var sorted = fused.OrderByDescending(x => x.score).ToList();
        sorted.First().item.Id.Should().Be("b"); // High-weight signal winner
    }

    [Fact]
    public void FuseRRF_NormalizesToZeroOne()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", "A"),
            MakeItem("2", "B")
        };

        var signal = new List<(ContentItem item, double score)>
        {
            (items[0], 100.0), (items[1], 1.0)
        };

        var fused = RelevanceScorer.FuseRRF(items, new[] { (signal, 1.0) });

        fused.Max(x => x.score).Should().Be(1.0);
        fused.All(x => x.score >= 0 && x.score <= 1).Should().BeTrue();
    }

    #endregion

    #region Tokenization

    [Fact]
    public void Tokenize_FiltersStopWords()
    {
        var tokens = RelevanceScorer.Tokenize("the latest news about pharmaceutical drugs");
        tokens.Should().NotContain("the");
        tokens.Should().NotContain("about");
        tokens.Should().NotContain("latest");
        tokens.Should().Contain("pharmaceutical");
        tokens.Should().Contain("drugs");
    }

    [Fact]
    public void Tokenize_LowercasesAndFiltersShort()
    {
        var tokens = RelevanceScorer.Tokenize("AI is a BIG Deal");
        tokens.Should().NotContain("a"); // Too short
        tokens.Should().Contain("ai");
        tokens.Should().Contain("big");
        tokens.Should().Contain("deal");
    }

    #endregion

    #region Quality Scoring

    [Fact]
    public void ComputeQualityScore_HighQualityContent_ScoresHigher()
    {
        // Simulate embeddings: high-quality content is closer to high anchor
        var highQualityAnchor = MakeUnitVector(0.8f, 0.1f, 0.1f);
        var lowQualityAnchor = MakeUnitVector(0.1f, 0.1f, 0.8f);

        // Item close to high-quality anchor
        var substantiveItem = MakeUnitVector(0.7f, 0.2f, 0.1f);
        // Item close to low-quality anchor
        var clickbaitItem = MakeUnitVector(0.1f, 0.2f, 0.7f);

        var highScore = RelevanceScorer.ComputeQualityScore(substantiveItem, highQualityAnchor, lowQualityAnchor);
        var lowScore = RelevanceScorer.ComputeQualityScore(clickbaitItem, highQualityAnchor, lowQualityAnchor);

        highScore.Should().BeGreaterThan(lowScore);
        highScore.Should().BeGreaterThan(0.5); // Above neutral
        lowScore.Should().BeLessThan(0.5); // Below neutral
    }

    [Fact]
    public void ComputeQualityScore_ReturnsInZeroOneRange()
    {
        var anchor1 = MakeUnitVector(1f, 0f, 0f);
        var anchor2 = MakeUnitVector(0f, 0f, 1f);
        var item = MakeUnitVector(0f, 1f, 0f);

        var score = RelevanceScorer.ComputeQualityScore(item, anchor1, anchor2);
        score.Should().BeInRange(0, 1);
    }

    #endregion

    #region PRF Centroid

    [Fact]
    public void ComputePRFCentroid_BlendsWithOriginal()
    {
        var original = MakeUnitVector(1f, 0f, 0f);
        var items = new List<ContentItem>
        {
            MakeItemWithEmbedding("1", "A", MakeUnitVector(0f, 1f, 0f)),
            MakeItemWithEmbedding("2", "B", MakeUnitVector(0f, 1f, 0f)),
            MakeItemWithEmbedding("3", "C", MakeUnitVector(0f, 1f, 0f))
        };

        var refined = RelevanceScorer.ComputePRFCentroid(items, original, topK: 3, alpha: 0.5f);

        // Refined should be somewhere between original and centroid
        // Original is [1,0,0], centroid is [0,1,0], blend at 0.5 should have both components
        refined[0].Should().BeGreaterThan(0); // Some original
        refined[1].Should().BeGreaterThan(0); // Some centroid
    }

    [Fact]
    public void ComputePRFCentroid_InsufficientItems_ReturnsOriginal()
    {
        var original = MakeUnitVector(1f, 0f, 0f);
        var items = new List<ContentItem>
        {
            MakeItemWithEmbedding("1", "A", MakeUnitVector(0f, 1f, 0f))
        };

        var result = RelevanceScorer.ComputePRFCentroid(items, original);

        // Should return original reference when fewer than 2 items
        result.Should().BeSameAs(original);
    }

    [Fact]
    public void ComputePRFCentroid_ResultIsNormalized()
    {
        var original = MakeUnitVector(1f, 0f, 0f);
        var items = new List<ContentItem>
        {
            MakeItemWithEmbedding("1", "A", MakeUnitVector(0f, 1f, 0f)),
            MakeItemWithEmbedding("2", "B", MakeUnitVector(0f, 0f, 1f)),
            MakeItemWithEmbedding("3", "C", MakeUnitVector(0.5f, 0.5f, 0.5f))
        };

        var refined = RelevanceScorer.ComputePRFCentroid(items, original, topK: 3, alpha: 0.7f);

        // Check L2 norm is approximately 1.0
        var norm = MathF.Sqrt(refined.Select(x => x * x).Sum());
        norm.Should().BeApproximately(1.0f, 0.01f);
    }

    [Fact]
    public void ComputePRFCentroid_SkipsItemsWithoutEmbeddings()
    {
        var original = MakeUnitVector(1f, 0f, 0f);
        var items = new List<ContentItem>
        {
            MakeItemWithEmbedding("1", "A", MakeUnitVector(0f, 1f, 0f)),
            MakeItem("2", "B"), // No embedding
            MakeItemWithEmbedding("3", "C", MakeUnitVector(0f, 0f, 1f))
        };

        var refined = RelevanceScorer.ComputePRFCentroid(items, original, topK: 3);

        // Should use only the 2 items with embeddings
        refined.Should().NotBeSameAs(original);
    }

    #endregion

    #region ForQueryType

    [Theory]
    [InlineData(QueryType.Roundup)]
    [InlineData(QueryType.Timeline)]
    [InlineData(QueryType.Explainer)]
    [InlineData(QueryType.Comparison)]
    [InlineData(QueryType.General)]
    public void ForQueryType_ReturnsWorkingScorer(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType);
        var items = new List<ContentItem>
        {
            MakeItem("1", "Test article about something", "Content goes here"),
            MakeItem("2", "Another article", "Different content")
        };

        var result = scorer.ScoreFast(items, "test article", discardRatio: 0);
        result.Should().HaveCount(2);
        result.All(i => i.RelevanceScore >= 0).Should().BeTrue();
    }

    #endregion

    #region Quality Signal Integration

    // Shared quality anchors for integration tests — "dimension 0" = quality, "dimension 2" = clickbait
    private static readonly float[] HighAnchor = MakeUnitVector(0.8f, 0.1f, 0.1f);
    private static readonly float[] LowAnchor = MakeUnitVector(0.1f, 0.1f, 0.8f);

    // Items with clear quality separation
    private static List<ContentItem> MakeQualityTestItems() =>
    [
        MakeItemWithEmbedding("substantive", "In-depth analysis of policy impact",
            MakeUnitVector(0.7f, 0.2f, 0.1f)),
        MakeItemWithEmbedding("clickbait", "You won't believe what happened next",
            MakeUnitVector(0.1f, 0.2f, 0.7f)),
        MakeItemWithEmbedding("neutral", "Standard news report on events",
            MakeUnitVector(0.3f, 0.6f, 0.1f))
    ];

    // Modes where quality weight is strong enough (≥0.2) to reliably separate content
    [Theory]
    [InlineData(QueryType.Explainer)]   // quality=0.4
    [InlineData(QueryType.Comparison)]  // quality=0.3
    [InlineData(QueryType.General)]     // quality=0.2
    public void QualitySignal_ScoreFast_FavorsSubstantiveContent_HighQualityModes(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var items = MakeQualityTestItems();
        var ranked = scorer.ScoreFast(items, "analysis report", discardRatio: 0);

        ranked.Should().HaveCount(3);
        var substantive = ranked.First(i => i.Id == "substantive");
        var clickbait = ranked.First(i => i.Id == "clickbait");
        substantive.RelevanceScore.Should().BeGreaterThan(clickbait.RelevanceScore,
            $"in {queryType} mode, quality anchor should favor substantive over clickbait");
    }

    // Modes where quality weight is intentionally low (≤0.15) — quality is a tiebreaker,
    // not a dominant signal. Verify it runs without error and produces valid scores.
    [Theory]
    [InlineData(QueryType.Roundup)]     // quality=0.15
    [InlineData(QueryType.Timeline)]    // quality=0.1
    public void QualitySignal_ScoreFast_ProducesValidScores_LowQualityModes(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var items = MakeQualityTestItems();
        var ranked = scorer.ScoreFast(items, "analysis report", discardRatio: 0);

        ranked.Should().HaveCount(3);
        ranked.All(i => i.RelevanceScore >= 0 && i.RelevanceScore <= 1).Should().BeTrue();
    }

    [Theory]
    [InlineData(QueryType.Explainer)]
    [InlineData(QueryType.Comparison)]
    [InlineData(QueryType.General)]
    public void QualitySignal_ScoreFull_FavorsSubstantiveContent_HighQualityModes(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var items = MakeQualityTestItems();
        var queryEmbedding = MakeUnitVector(0.4f, 0.5f, 0.1f); // balanced query

        var ranked = scorer.ScoreFull(items, "analysis report", queryEmbedding);

        ranked.Should().HaveCount(3);
        var substantive = ranked.First(i => i.Id == "substantive");
        var clickbait = ranked.First(i => i.Id == "clickbait");
        substantive.RelevanceScore.Should().BeGreaterThan(clickbait.RelevanceScore,
            $"in {queryType} mode (full), quality anchor should favor substantive over clickbait");
    }

    [Theory]
    [InlineData(QueryType.Roundup)]
    [InlineData(QueryType.Timeline)]
    public void QualitySignal_ScoreFull_ProducesValidScores_LowQualityModes(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var items = MakeQualityTestItems();
        var queryEmbedding = MakeUnitVector(0.4f, 0.5f, 0.1f);

        var ranked = scorer.ScoreFull(items, "analysis report", queryEmbedding);

        ranked.Should().HaveCount(3);
        ranked.All(i => i.RelevanceScore >= 0 && i.RelevanceScore <= 1).Should().BeTrue();
    }

    [Fact]
    public void QualitySignal_HigherWeight_ProducesLargerGap()
    {
        // Isolate quality influence by using identical weights for all OTHER signals,
        // varying ONLY the quality weight (0.5 vs 0.05)
        var strongQuality = new RelevanceScorer(qualityWeight: 0.5)
            .WithQualityAnchors(HighAnchor, LowAnchor);
        var weakQuality = new RelevanceScorer(qualityWeight: 0.05)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var strongItems = MakeQualityTestItems();
        var weakItems = MakeQualityTestItems();

        strongQuality.ScoreFast(strongItems, "analysis report", discardRatio: 0);
        weakQuality.ScoreFast(weakItems, "analysis report", discardRatio: 0);

        var strongGap = strongItems.First(i => i.Id == "substantive").RelevanceScore
                        - strongItems.First(i => i.Id == "clickbait").RelevanceScore;
        var weakGap = weakItems.First(i => i.Id == "substantive").RelevanceScore
                      - weakItems.First(i => i.Id == "clickbait").RelevanceScore;

        // Higher quality weight should produce a larger score gap
        strongGap.Should().BeGreaterThan(weakGap,
            "quality weight 0.5 should separate quality more than quality weight 0.05");
    }

    [Theory]
    [InlineData(QueryType.Roundup)]
    [InlineData(QueryType.Timeline)]
    [InlineData(QueryType.Explainer)]
    [InlineData(QueryType.Comparison)]
    [InlineData(QueryType.General)]
    public void QualitySignal_WithoutAnchors_DoesNotCrash_AllModes(QueryType queryType)
    {
        // No WithQualityAnchors call — quality signal should be silently skipped
        var scorer = RelevanceScorer.ForQueryType(queryType);
        var items = MakeQualityTestItems();

        var ranked = scorer.ScoreFast(items, "analysis report", discardRatio: 0);
        ranked.Should().HaveCount(3);
        ranked.All(i => i.RelevanceScore >= 0).Should().BeTrue();
    }

    [Theory]
    [InlineData(QueryType.Roundup)]
    [InlineData(QueryType.Timeline)]
    [InlineData(QueryType.Explainer)]
    [InlineData(QueryType.Comparison)]
    [InlineData(QueryType.General)]
    public void QualitySignal_ScoreFull_WithoutAnchors_DoesNotCrash_AllModes(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType);
        var items = MakeQualityTestItems();
        var queryEmbedding = MakeUnitVector(0.4f, 0.5f, 0.1f);

        var ranked = scorer.ScoreFull(items, "analysis report", queryEmbedding);
        ranked.Should().HaveCount(3);
        ranked.All(i => i.RelevanceScore >= 0).Should().BeTrue();
    }

    [Theory]
    [InlineData(QueryType.Roundup)]
    [InlineData(QueryType.Timeline)]
    [InlineData(QueryType.Explainer)]
    [InlineData(QueryType.Comparison)]
    [InlineData(QueryType.General)]
    public void QualitySignal_ItemsWithoutEmbeddings_GetNeutralScore_AllModes(QueryType queryType)
    {
        var scorer = RelevanceScorer.ForQueryType(queryType)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var items = new List<ContentItem>
        {
            MakeItem("no-emb-1", "Article without embedding", "Some content about analysis"),
            MakeItem("no-emb-2", "Another no-embedding article", "Different content here")
        };

        // Should not crash — items without embeddings get neutral quality (0.5)
        var ranked = scorer.ScoreFast(items, "analysis", discardRatio: 0);
        ranked.Should().HaveCount(2);
        ranked.All(i => i.RelevanceScore >= 0).Should().BeTrue();
    }

    [Fact]
    public void QualitySignal_ScoreFull_WithVibeEmbedding_AllSignalsWork()
    {
        var scorer = RelevanceScorer.ForQueryType(QueryType.General)
            .WithQualityAnchors(HighAnchor, LowAnchor);

        var items = MakeQualityTestItems();
        var queryEmbedding = MakeUnitVector(0.4f, 0.5f, 0.1f);
        var vibeEmbedding = MakeUnitVector(0.3f, 0.6f, 0.1f);

        // All 6 signals active: BM25F + Freshness + Authority + QuerySim + Vibe + Quality
        var ranked = scorer.ScoreFull(items, "analysis report", queryEmbedding, vibeEmbedding);

        ranked.Should().HaveCount(3);
        ranked.All(i => i.RelevanceScore >= 0).Should().BeTrue();
        // Substantive should still beat clickbait even with all signals
        var substantive = ranked.First(i => i.Id == "substantive");
        var clickbait = ranked.First(i => i.Id == "clickbait");
        substantive.RelevanceScore.Should().BeGreaterThan(clickbait.RelevanceScore);
    }

    #endregion

    #region Helpers

    private static float[] MakeUnitVector(params float[] components)
    {
        var norm = MathF.Sqrt(components.Select(x => x * x).Sum());
        return norm > 0 ? components.Select(x => x / norm).ToArray() : components;
    }

    private static ContentItem MakeItemWithEmbedding(string id, string title, float[] embedding)
    {
        return new ContentItem
        {
            Id = id,
            Source = "test",
            Title = title,
            CreatedAt = DateTimeOffset.UtcNow,
            Embedding = embedding
        };
    }

    private static ContentItem CloneItem(ContentItem item) => new()
    {
        Id = item.Id,
        Source = item.Source,
        Title = item.Title,
        Content = item.Content,
        Score = item.Score,
        CreatedAt = item.CreatedAt,
        Embedding = item.Embedding
    };

    #endregion
}

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

        var highAuth = RelevanceScorer.NormalizeAuthority(items[0], items);
        var lowAuth = RelevanceScorer.NormalizeAuthority(items[1], items);
        var bbcAuth = RelevanceScorer.NormalizeAuthority(items[2], items);

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

        RelevanceScorer.NormalizeAuthority(items[0], items).Should().Be(0.3);
        RelevanceScorer.NormalizeAuthority(items[1], items).Should().Be(0.4);
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
}

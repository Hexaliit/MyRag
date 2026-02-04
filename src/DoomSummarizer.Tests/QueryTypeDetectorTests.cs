using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for QueryTypeDetector: roundup detection, date-gating, topic drift.
/// </summary>
public class QueryTypeDetectorTests
{
    // --- Roundup detection ---

    [Theory]
    [InlineData("most interesting technology stories from today")]
    [InlineData("what happened today in tech")]
    [InlineData("today's top headlines")]
    [InlineData("latest AI news")]
    [InlineData("this week in .NET")]
    [InlineData("trending stories")]
    [InlineData("top 10 tech stories")]
    [InlineData("what's new in rust")]
    [InlineData("best of this week")]
    [InlineData("daily digest")]
    [InlineData("this morning's headlines")]
    [InlineData("interesting stories")]
    public void Detect_RoundupQueries_ReturnsRoundup(string query)
    {
        QueryTypeDetector.Detect(query).Should().Be(QueryType.Roundup);
    }

    [Theory]
    [InlineData("how does TCP work")]
    [InlineData("explain quantum computing")]
    [InlineData("history of the internet")]
    [InlineData("rust vs go comparison")]
    public void Detect_NonRoundupQueries_DoesNotReturnRoundup(string query)
    {
        QueryTypeDetector.Detect(query).Should().NotBe(QueryType.Roundup);
    }

    // --- Date-gating (uses SentinelIntent, not query patterns) ---

    [Theory]
    [InlineData("today")]
    [InlineData("breaking")]
    [InlineData("week")]
    public void ImpliesDateGating_TodayTimeSensitivity_ReturnsTrue(string timeSensitivity)
    {
        var intent = new SentinelIntent { TimeSensitivity = timeSensitivity };
        QueryTypeDetector.ImpliesDateGating(intent).Should().BeTrue();
    }

    [Fact]
    public void ImpliesDateGating_RequiresFresh_ReturnsTrue()
    {
        var intent = new SentinelIntent { RequiresFresh = true };
        QueryTypeDetector.ImpliesDateGating(intent).Should().BeTrue();
    }

    [Fact]
    public void ImpliesDateGating_WithDateRange_ReturnsTrue()
    {
        var intent = new SentinelIntent { DateRange = new DateRangeIntent { Start = "2024-01-20" } };
        QueryTypeDetector.ImpliesDateGating(intent).Should().BeTrue();
    }

    [Fact]
    public void ImpliesDateGating_NullIntent_ReturnsFalse()
    {
        QueryTypeDetector.ImpliesDateGating(null).Should().BeFalse();
    }

    [Fact]
    public void ImpliesDateGating_NoTemporalSignals_ReturnsFalse()
    {
        var intent = new SentinelIntent
        {
            Intent = "research",
            TimeSensitivity = "evergreen"
        };
        QueryTypeDetector.ImpliesDateGating(intent).Should().BeFalse();
    }

    // --- Topic drift ---

    [Fact]
    public void IsTopicDrift_OnThisDay_ReturnsTrue()
    {
        var item = new ContentItem
        {
            Id = "test_1", Source = "bbc", Title = "On this day: Mark Twain born",
            Content = "On this day in 1835, Mark Twain was born in Missouri."
        };
        QueryTypeDetector.IsTopicDrift(item).Should().BeTrue();
    }

    [Fact]
    public void IsTopicDrift_TodayInHistory_ReturnsTrue()
    {
        var item = new ContentItem
        {
            Id = "test_2", Source = "bbc", Title = "Today in History: Apollo 11",
            Content = "Today in history, 1969, Apollo 11 launched."
        };
        QueryTypeDetector.IsTopicDrift(item).Should().BeTrue();
    }

    [Fact]
    public void IsTopicDrift_NormalArticle_ReturnsFalse()
    {
        var item = new ContentItem
        {
            Id = "test_3", Source = "verge", Title = "New AI chip from NVIDIA announced today",
            Content = "NVIDIA today announced its latest GPU architecture."
        };
        QueryTypeDetector.IsTopicDrift(item).Should().BeFalse();
    }

    // --- Freshness multiplier ---

    [Fact]
    public void GetFreshnessMultiplier_RecentItem_Returns1()
    {
        var item = new ContentItem
        {
            Id = "test_4", Source = "hn", Title = "Fresh news",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        QueryTypeDetector.GetFreshnessMultiplier(item, TimeSpan.FromHours(48))
            .Should().Be(1.0);
    }

    [Fact]
    public void GetFreshnessMultiplier_OldItem_ReturnsDemoted()
    {
        var item = new ContentItem
        {
            Id = "test_5", Source = "hn", Title = "Old news",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-72)
        };
        QueryTypeDetector.GetFreshnessMultiplier(item, TimeSpan.FromHours(48))
            .Should().Be(0.5);
    }

    [Fact]
    public void GetFreshnessMultiplier_VeryOldItem_ReturnsSeverelyDemoted()
    {
        var item = new ContentItem
        {
            Id = "test_6", Source = "hn", Title = "Ancient news",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7)
        };
        QueryTypeDetector.GetFreshnessMultiplier(item, TimeSpan.FromHours(48))
            .Should().Be(0.2);
    }

    // --- Source quality multipliers ---

    [Theory]
    [InlineData("https://www.bbc.co.uk/news/technology-12345")]
    [InlineData("https://www.theverge.com/2026/1/27/ai-news")]
    [InlineData("https://techcrunch.com/2026/01/startup")]
    public void GetSourceQualityMultiplier_Roundup_BoostsNewsSources(string url)
    {
        QueryTypeDetector.GetSourceQualityMultiplier(QueryType.Roundup, url)
            .Should().BeGreaterThan(1.0);
    }

    [Theory]
    [InlineData("https://arxiv.org/abs/2511.18261")]
    [InlineData("https://en.wikipedia.org/wiki/AI")]
    public void GetSourceQualityMultiplier_Roundup_DemotesAcademic(string url)
    {
        QueryTypeDetector.GetSourceQualityMultiplier(QueryType.Roundup, url)
            .Should().BeLessThan(1.0);
    }
}
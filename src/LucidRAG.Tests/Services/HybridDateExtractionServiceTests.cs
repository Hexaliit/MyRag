using FluentAssertions;
using LucidRAG.Services;

namespace LucidRAG.Tests.Services;

/// <summary>
///     Unit tests for HybridDateExtractionService.
///     Tests relative dates, quarters, seasons, fiscal years, and NER integration.
/// </summary>
public class HybridDateExtractionServiceTests
{
    private readonly DateTimeOffset _referenceDate = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly HybridDateExtractionService _service;

    public HybridDateExtractionServiceTests()
    {
        var regexService = new DateExtractionService();
        _service = new HybridDateExtractionService(regexService);
    }

    #region Quarter Tests

    [Theory]
    [InlineData("Q1 2024", 2024, 1, 1)]
    [InlineData("Q2 2024", 2024, 4, 1)]
    [InlineData("Q3 2024", 2024, 7, 1)]
    [InlineData("Q4 2024", 2024, 10, 1)]
    public void ExtractAllDates_Quarter_ReturnsQuarterStart(string input, int year, int month, int day)
    {
        // Act
        var results = _service.ExtractAllDates(input);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Year.Should().Be(year);
        results[0].ParsedDate!.Value.Month.Should().Be(month);
        results[0].ParsedDate!.Value.Day.Should().Be(day);
        results[0].DatePrecision.Should().Be(DatePrecision.Quarter);
        results[0].Format.Should().Be("Quarter");
    }

    #endregion

    #region Season Tests

    [Theory]
    [InlineData("spring 2024", 3)]
    [InlineData("summer 2024", 6)]
    [InlineData("fall 2024", 9)]
    [InlineData("autumn 2024", 9)]
    [InlineData("winter 2024", 12)]
    public void ExtractAllDates_Season_ReturnsSeasonStart(string input, int expectedMonth)
    {
        // Act
        var results = _service.ExtractAllDates(input);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        results[0].DatePrecision.Should().Be(DatePrecision.Season);
        results[0].IsAmbiguous.Should().BeTrue(); // Seasons are hemisphere-dependent
    }

    #endregion

    #region Fiscal Year Tests

    [Theory]
    [InlineData("FY24", 2024)]
    [InlineData("FY 2024", 2024)]
    [InlineData("fiscal year 2024", 2024)]
    [InlineData("Fiscal Year '24", 2024)]
    public void ExtractAllDates_FiscalYear_ReturnsYear(string input, int expectedYear)
    {
        // Act
        var results = _service.ExtractAllDates(input);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        results[0].DatePrecision.Should().Be(DatePrecision.Year);
        results[0].IsAmbiguous.Should().BeTrue(); // FY start varies by company
    }

    #endregion

    #region Simple Day Tests

    [Theory]
    [InlineData("today", 0)]
    [InlineData("yesterday", -1)]
    [InlineData("tomorrow", 1)]
    public void ExtractAllDates_SimpleDays_ReturnsCorrectDate(string input, int dayOffset)
    {
        // Act
        var results = _service.ExtractAllDates(input, referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Date.Should().Be(_referenceDate.Date.AddDays(dayOffset));
        results[0].IsRelative.Should().BeTrue();
        results[0].Format.Should().Be("Relative");
        results[0].DatePrecision.Should().Be(DatePrecision.Day);
    }

    [Fact]
    public void ExtractAllDates_Today_HighConfidence()
    {
        // Act
        var results = _service.ExtractAllDates("today", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].Confidence.Should().Be(1.0);
    }

    #endregion

    #region Relative Weekday Tests

    [Fact]
    public void ExtractAllDates_LastMonday_ReturnsCorrectDate()
    {
        // Arrange - Reference: Saturday June 15, 2024
        // Last Monday would be June 10, 2024

        // Act
        var results = _service.ExtractAllDates("last Monday", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday);
        results[0].ParsedDate!.Value.Should().BeBefore(_referenceDate);
        results[0].IsRelative.Should().BeTrue();
        results[0].Format.Should().Be("RelativeWeekday");
    }

    [Fact]
    public void ExtractAllDates_NextFriday_ReturnsCorrectDate()
    {
        // Act
        var results = _service.ExtractAllDates("next Friday", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.DayOfWeek.Should().Be(DayOfWeek.Friday);
        results[0].ParsedDate!.Value.Should().BeAfter(_referenceDate);
    }

    [Fact]
    public void ExtractAllDates_ThisWednesday_ReturnsCorrectDate()
    {
        // Act
        var results = _service.ExtractAllDates("this Wednesday", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.DayOfWeek.Should().Be(DayOfWeek.Wednesday);
    }

    #endregion

    #region Relative Period Tests

    [Fact]
    public void ExtractAllDates_LastWeek_ReturnsWeekStart()
    {
        // Act
        var results = _service.ExtractAllDates("last week", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].DatePrecision.Should().Be(DatePrecision.Week);
        results[0].ParsedDate!.Value.Should().BeBefore(_referenceDate);
    }

    [Fact]
    public void ExtractAllDates_NextMonth_ReturnsMonthStart()
    {
        // Act
        var results = _service.ExtractAllDates("next month", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].DatePrecision.Should().Be(DatePrecision.Month);
        results[0].ParsedDate!.Value.Month.Should().Be(_referenceDate.Month + 1);
        results[0].ParsedDate!.Value.Day.Should().Be(1);
    }

    [Fact]
    public void ExtractAllDates_ThisYear_ReturnsYearStart()
    {
        // Act
        var results = _service.ExtractAllDates("this year", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].DatePrecision.Should().Be(DatePrecision.Year);
        results[0].ParsedDate!.Value.Year.Should().Be(_referenceDate.Year);
        results[0].ParsedDate!.Value.Month.Should().Be(1);
        results[0].ParsedDate!.Value.Day.Should().Be(1);
    }

    [Fact]
    public void ExtractAllDates_LastQuarter_ReturnsQuarterStart()
    {
        // Act
        var results = _service.ExtractAllDates("last quarter", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].DatePrecision.Should().Be(DatePrecision.Quarter);
    }

    #endregion

    #region Relative Offset Tests

    [Fact]
    public void ExtractAllDates_ThreeDaysAgo_ReturnsCorrectDate()
    {
        // Act
        var results = _service.ExtractAllDates("3 days ago", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Date.Should().Be(_referenceDate.Date.AddDays(-3));
        results[0].IsRelative.Should().BeTrue();
    }

    [Fact]
    public void ExtractAllDates_TwoWeeksFromNow_ReturnsCorrectDate()
    {
        // Act
        var results = _service.ExtractAllDates("2 weeks from now", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Date.Should().Be(_referenceDate.Date.AddDays(14));
    }

    [Fact]
    public void ExtractAllDates_SixMonthsAgo_ReturnsCorrectDate()
    {
        // Act
        var results = _service.ExtractAllDates("6 months ago", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Month.Should().Be(12); // June - 6 = December of previous year
    }

    [Fact]
    public void ExtractAllDates_OneYearLater_ReturnsCorrectDate()
    {
        // Act
        var results = _service.ExtractAllDates("1 year later", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Year.Should().Be(_referenceDate.Year + 1);
    }

    #endregion

    #region Combined Tests (Absolute + Relative)

    [Fact]
    public void ExtractAllDates_MixedAbsoluteAndRelative_FindsAll()
    {
        // Arrange
        var text = "The project started on 2024-01-15 and should be completed by next Friday.";

        // Act
        var results = _service.ExtractAllDates(text, referenceDate: _referenceDate);

        // Assert
        results.Should().HaveCount(2);
        results.Should().Contain(r => r.Format == "ISO8601" && !r.IsRelative);
        results.Should().Contain(r => r.Format == "RelativeWeekday" && r.IsRelative);
    }

    [Fact]
    public void ExtractAllDates_QuarterAndAbsoluteDate_FindsAll()
    {
        // Arrange
        var text = "Q3 2024 targets were set in January 2024.";

        // Act
        var results = _service.ExtractAllDates(text);

        // Assert
        results.Should().HaveCount(2);
        results.Should().Contain(r => r.Format == "Quarter");
        results.Should().Contain(r => r.Format == "MonthYear");
    }

    [Fact]
    public void ExtractAllDates_MultiplePeriods_FindsAll()
    {
        // Arrange
        var text = "Review last week's data and plan for next month.";

        // Act
        var results = _service.ExtractAllDates(text, referenceDate: _referenceDate);

        // Assert
        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.IsRelative);
    }

    #endregion

    #region NER Integration Tests

    [Fact]
    public void ParseNerDateSpans_ValidIsoDate_ParsesCorrectly()
    {
        // Arrange
        var nerSpans = new[] { ("2024-06-15", 0, 0.95) };

        // Act
        var results = _service.ParseNerDateSpans(nerSpans, "2024-06-15");

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Year.Should().Be(2024);
        results[0].ParsedDate!.Value.Month.Should().Be(6);
        results[0].ParsedDate!.Value.Day.Should().Be(15);
        results[0].DetectionMethod.Should().Contain("ner");
    }

    [Fact]
    public void ParseNerDateSpans_RelativeDate_ParsesCorrectly()
    {
        // Arrange
        var nerSpans = new[] { ("last week", 10, 0.85) };

        // Act
        var results = _service.ParseNerDateSpans(nerSpans, "Meeting was last week.", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        results[0].IsRelative.Should().BeTrue();
        results[0].DetectionMethod.Should().Contain("ner");
    }

    [Fact]
    public void ParseNerDateSpans_UnparseableSpan_ReturnsLowConfidence()
    {
        // Arrange - NER detected "next Tuesday's deadline" which contains a date reference
        var nerSpans = new[] { ("next Tuesday's deadline", 0, 0.75) };

        // Act
        var results = _service.ParseNerDateSpans(nerSpans, "next Tuesday's deadline", referenceDate: _referenceDate);

        // Assert
        results.Should().ContainSingle();
        // Should find "next Tuesday" within the span
    }

    [Fact]
    public void ParseNerDateSpans_MultipleSpans_ParsesAll()
    {
        // Arrange
        var nerSpans = new[]
        {
            ("2024-01-15", 0, 0.95),
            ("Q2 2024", 20, 0.88),
            ("yesterday", 35, 0.92)
        };

        // Act
        var results = _service.ParseNerDateSpans(nerSpans, "From 2024-01-15 to Q2 2024, yesterday was...",
            referenceDate: _referenceDate);

        // Assert
        results.Should().HaveCount(3);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ExtractAllDates_EmptyString_ReturnsEmpty()
    {
        // Act
        var results = _service.ExtractAllDates("");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAllDates_NullString_ReturnsEmpty()
    {
        // Act
        var results = _service.ExtractAllDates(null!);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAllDates_NoReferenceDate_UsesUtcNow()
    {
        // Act
        var results = _service.ExtractAllDates("today");

        // Assert
        results.Should().ContainSingle();
        results[0].ParsedDate!.Value.Date.Should().Be(DateTimeOffset.UtcNow.Date);
    }

    [Fact]
    public void ExtractAllDates_OverlappingSpans_DoesNotDuplicate()
    {
        // Arrange - "Q1 2024" and "2024" could overlap
        var text = "Q1 2024 results";

        // Act
        var results = _service.ExtractAllDates(text);

        // Assert - Should not return both Q1 2024 and a standalone 2024
        results.Should().HaveCount(1);
        results[0].Format.Should().Be("Quarter");
    }

    #endregion

    #region Position Tracking Tests

    [Fact]
    public void ExtractAllDates_TracksPosition()
    {
        // Arrange
        var text = "Meeting on 2024-06-15 at noon.";

        // Act
        var results = _service.ExtractAllDates(text);

        // Assert
        results.Should().ContainSingle();
        results[0].StartIndex.Should().Be(11);
        results[0].EndIndex.Should().Be(21);
        results[0].OriginalText.Should().Be("2024-06-15");
    }

    [Fact]
    public void ExtractAllDates_MultiplePositions_InOrder()
    {
        // Arrange
        var text = "From yesterday to tomorrow";

        // Act
        var results = _service.ExtractAllDates(text, referenceDate: _referenceDate);

        // Assert
        results.Should().HaveCount(2);
        results[0].StartIndex.Should().BeLessThan(results[1].StartIndex);
    }

    #endregion
}
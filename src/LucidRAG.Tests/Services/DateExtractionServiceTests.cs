using FluentAssertions;
using LucidRAG.Services;

namespace LucidRAG.Tests.Services;

/// <summary>
///     Unit tests for DateExtractionService.
///     Tests various date formats, locale detection, and international date parsing.
/// </summary>
public class DateExtractionServiceTests
{
    private readonly DateExtractionService _service = new();

    #region ISO 8601 Format Tests

    [Theory]
    [InlineData("2024-01-15", 2024, 1, 15)]
    [InlineData("2024-12-31", 2024, 12, 31)]
    [InlineData("2023-06-01", 2023, 6, 1)]
    public void ExtractDates_Iso8601Format_ParsesCorrectly(string input, int expectedYear, int expectedMonth,
        int expectedDay)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate.Should().NotBeNull();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(expectedDay);
        result[0].Format.Should().Be("ISO8601");
        result[0].Confidence.Should().Be(1.0);
    }

    [Theory]
    [InlineData("2024-01-15T10:30:00Z")]
    [InlineData("2024-01-15T10:30:00+05:00")]
    [InlineData("2024-01-15T10:30:00-08:00")]
    public void ExtractDates_Iso8601WithTime_ParsesDatePortion(string input)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(2024);
        result[0].ParsedDate!.Value.Month.Should().Be(1);
        result[0].ParsedDate!.Value.Day.Should().Be(15);
    }

    #endregion

    #region Long Format Tests (Month Names)

    [Theory]
    [InlineData("January 15, 2024", 2024, 1, 15)]
    [InlineData("December 31, 2023", 2023, 12, 31)]
    [InlineData("Feb 28, 2024", 2024, 2, 28)]
    [InlineData("Mar. 1, 2024", 2024, 3, 1)]
    public void ExtractDates_MonthFirstFormat_ParsesCorrectly(string input, int expectedYear, int expectedMonth,
        int expectedDay)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(expectedDay);
        result[0].Format.Should().Be("MonthFirst");
        result[0].Confidence.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Theory]
    [InlineData("15 January 2024", 2024, 1, 15)]
    [InlineData("31 December 2023", 2023, 12, 31)]
    [InlineData("1st March 2024", 2024, 3, 1)]
    [InlineData("22nd February 2024", 2024, 2, 22)]
    public void ExtractDates_DayFirstFormat_ParsesCorrectly(string input, int expectedYear, int expectedMonth,
        int expectedDay)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(expectedDay);
        result[0].Format.Should().Be("DayFirst");
    }

    #endregion

    #region Ambiguous Format Tests

    [Theory]
    [InlineData("25/01/2024", LocaleHint.European, 2024, 1, 25)] // Day > 12, must be DMY
    [InlineData("01/25/2024", LocaleHint.American, 2024, 1, 25)] // Day > 12, must be MDY
    public void ExtractDates_UnambiguousSlashFormat_ParsesCorrectly(string input, LocaleHint locale, int expectedYear,
        int expectedMonth, int expectedDay)
    {
        // Act
        var result = _service.ExtractDates(input, locale);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(expectedDay);
        result[0].Confidence.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Theory]
    [InlineData("01/02/2024", LocaleHint.American, 2024, 1, 2)] // MM/DD
    [InlineData("01/02/2024", LocaleHint.European, 2024, 2, 1)] // DD/MM
    public void ExtractDates_AmbiguousSlashFormat_UsesLocaleHint(string input, LocaleHint locale, int expectedYear,
        int expectedMonth, int expectedDay)
    {
        // Act
        var result = _service.ExtractDates(input, locale);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(expectedDay);
        result[0].IsAmbiguous.Should().BeTrue(); // Both values <= 12
    }

    [Theory]
    [InlineData("15.01.2024", 2024, 1, 15)]
    [InlineData("31.12.2023", 2023, 12, 31)]
    public void ExtractDates_DotFormat_ParsesAsDMY(string input, int expectedYear, int expectedMonth, int expectedDay)
    {
        // Act (dot format is always DMY in Europe)
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(expectedDay);
        result[0].Format.Should().Be("DotNumeric");
    }

    #endregion

    #region Locale Detection Tests

    [Fact]
    public void DetectLocale_BritishSpellings_ReturnsEuropean()
    {
        // Arrange
        var text = "The colour of the document was favourably received by the organisation.";

        // Act
        var locale = _service.DetectLocale(text);

        // Assert
        locale.Should().Be(LocaleHint.European);
    }

    [Fact]
    public void DetectLocale_AmericanSpellings_ReturnsAmerican()
    {
        // Arrange
        var text = "The color of the document was favorably received by the organization.";

        // Act
        var locale = _service.DetectLocale(text);

        // Assert
        locale.Should().Be(LocaleHint.American);
    }

    [Fact]
    public void DetectLocale_UnambiguousDMYDates_ReturnsEuropean()
    {
        // Arrange (25 > 12, so must be day, meaning DMY format)
        var text = "Meeting on 25/01/2024 and 28/02/2024";

        // Act
        var locale = _service.DetectLocale(text);

        // Assert
        locale.Should().Be(LocaleHint.European);
    }

    [Fact]
    public void DetectLocale_UnambiguousMDYDates_ReturnsAmerican()
    {
        // Arrange (25 > 12, so second must be day, meaning MDY format)
        var text = "Meeting on 01/25/2024 and 02/28/2024";

        // Act
        var locale = _service.DetectLocale(text);

        // Assert
        locale.Should().Be(LocaleHint.American);
    }

    #endregion

    #region Year-Month Format Tests

    [Theory]
    [InlineData("2024-01", 2024, 1)]
    [InlineData("2023-12", 2023, 12)]
    public void ExtractDates_YearMonthFormat_DefaultsToFirstOfMonth(string input, int expectedYear, int expectedMonth)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(1);
        result[0].Format.Should().Be("YearMonth");
    }

    [Theory]
    [InlineData("January 2024", 2024, 1)]
    [InlineData("Dec 2023", 2023, 12)]
    public void ExtractDates_MonthYearFormat_DefaultsToFirstOfMonth(string input, int expectedYear, int expectedMonth)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Year.Should().Be(expectedYear);
        result[0].ParsedDate!.Value.Month.Should().Be(expectedMonth);
        result[0].ParsedDate!.Value.Day.Should().Be(1);
        result[0].Format.Should().Be("MonthYear");
    }

    #endregion

    #region Multiple Dates in Text

    [Fact]
    public void ExtractDates_MultipleDatesInText_ReturnsAllInOrder()
    {
        // Arrange
        var text = "The project started on 2024-01-15 and ended on 2024-03-20.";

        // Act
        var result = _service.ExtractDates(text);

        // Assert
        result.Should().HaveCount(2);
        result[0].ParsedDate!.Value.Should().Be(new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero));
        result[1].ParsedDate!.Value.Should().Be(new DateTimeOffset(2024, 3, 20, 0, 0, 0, TimeSpan.Zero));
        result[0].StartIndex.Should().BeLessThan(result[1].StartIndex);
    }

    [Fact]
    public void ExtractDates_MixedFormats_ParsesAll()
    {
        // Arrange
        var text = "From 2024-01-15 to January 31, 2024 with a review on 15/02/2024.";

        // Act
        var result = _service.ExtractDates(text, LocaleHint.European);

        // Assert
        result.Should().HaveCount(3);
        result.Select(r => r.Format).Should().Contain("ISO8601");
        result.Select(r => r.Format).Should().Contain("MonthFirst");
        result.Select(r => r.Format).Should().Contain("SlashNumeric");
    }

    #endregion

    #region Normalization Tests

    [Fact]
    public void NormalizeDatesInText_ReplacesWithIsoFormat()
    {
        // Arrange
        var text = "Meeting on January 15, 2024 at 10am.";

        // Act
        var normalized = _service.NormalizeDatesInText(text);

        // Assert
        normalized.Should().Contain("2024-01-15");
        normalized.Should().NotContain("January");
    }

    [Fact]
    public void NormalizeDatesInText_PreservesNonDateText()
    {
        // Arrange
        var text = "Project started 2024-01-15, budget $1,000,000";

        // Act
        var normalized = _service.NormalizeDatesInText(text);

        // Assert
        normalized.Should().Contain("$1,000,000");
        normalized.Should().Contain("2024-01-15");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ExtractDates_EmptyString_ReturnsEmpty()
    {
        // Act
        var result = _service.ExtractDates("");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractDates_NullString_ReturnsEmpty()
    {
        // Act
        var result = _service.ExtractDates(null!);

        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("2024-13-01")] // Invalid month 13
    public void ExtractDates_InvalidDates_ReturnsEmpty(string input)
    {
        // Act
        var result = _service.ExtractDates(input);

        // Assert - Invalid full ISO date should not parse
        result.Where(r => r.Format == "ISO8601").Should().BeEmpty();
    }

    [Fact]
    public void ExtractDates_InvalidFeb30_DoesNotParseAsIso()
    {
        // Act - "2024-02-30" has invalid day 30 for Feb, but "2024-02" is valid year-month
        var result = _service.ExtractDates("2024-02-30");

        // Assert - Should NOT find ISO8601 full date, but may find year-month
        result.Where(r => r.Format == "ISO8601").Should().BeEmpty();
    }

    [Fact]
    public void ExtractDates_NonLeapYearFeb29_DoesNotParseAsIso()
    {
        // Act - "2023-02-29" is invalid (2023 not leap), but "2023-02" is valid year-month
        var result = _service.ExtractDates("2023-02-29");

        // Assert - Should NOT find ISO8601 full date
        result.Where(r => r.Format == "ISO8601").Should().BeEmpty();
    }

    [Fact]
    public void ExtractDates_LeapYearFeb29_IsValid()
    {
        // Arrange (2024 is a leap year)
        var text = "2024-02-29";

        // Act
        var result = _service.ExtractDates(text);

        // Assert
        result.Should().ContainSingle();
        result[0].ParsedDate!.Value.Day.Should().Be(29);
    }

    #endregion

    #region ParseDate Direct Tests

    [Theory]
    [InlineData("2024-06-15", 2024, 6, 15)]
    [InlineData("March 10, 2023", 2023, 3, 10)]
    public void ParseDate_ValidDate_ReturnsDateTimeOffset(string input, int year, int month, int day)
    {
        // Act
        var result = _service.ParseDate(input);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Year.Should().Be(year);
        result!.Value.Month.Should().Be(month);
        result!.Value.Day.Should().Be(day);
    }

    [Fact]
    public void ParseDate_InvalidDate_ReturnsNull()
    {
        // Act
        var result = _service.ParseDate("not a date");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Original Text Preservation

    [Fact]
    public void ExtractDates_PreservesOriginalText()
    {
        // Arrange
        var text = "The event is on January 15th, 2024";

        // Act
        var result = _service.ExtractDates(text);

        // Assert
        result.Should().ContainSingle();
        result[0].OriginalText.Should().Be("January 15th, 2024");
    }

    [Fact]
    public void ExtractDates_TracksPositionInText()
    {
        // Arrange
        var text = "Start: 2024-01-01 End: 2024-12-31";
        //         0123456789...

        // Act
        var result = _service.ExtractDates(text);

        // Assert
        result.Should().HaveCount(2);
        result[0].StartIndex.Should().Be(7); // "Start: " = 7 chars
        result[0].EndIndex.Should().Be(17); // "2024-01-01" = 10 chars
        // "Start: 2024-01-01 End: " = 23 chars
        result[1].StartIndex.Should().Be(23);
        result[1].EndIndex.Should().Be(33);
    }

    #endregion
}
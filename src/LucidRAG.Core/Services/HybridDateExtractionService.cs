using System.Globalization;
using System.Text.RegularExpressions;

namespace LucidRAG.Services;

/// <summary>
/// Hybrid date extraction that combines NER-based detection with regex parsing.
/// NER identifies date spans (including relative dates like "last Tuesday"),
/// then regex patterns parse them into structured DateTimeOffset values.
/// </summary>
public partial class HybridDateExtractionService : IHybridDateExtractionService
{
    private readonly IDateExtractionService _regexService;

    // Relative date patterns (source-generated for performance)
    [GeneratedRegex(@"\b(today|yesterday|tomorrow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SimpleDayPattern();

    [GeneratedRegex(@"\b(last|next|this)\s+(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeWeekdayPattern();

    [GeneratedRegex(@"\b(last|next|this)\s+(week|month|year|quarter)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativePeriodPattern();

    [GeneratedRegex(@"\b(\d+)\s+(days?|weeks?|months?|years?)\s+(ago|from\s+now|later)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeOffsetPattern();

    [GeneratedRegex(@"\bQ([1-4])\s+(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuarterPattern();

    [GeneratedRegex(@"\b(early|mid|late)\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApproximateMonthPattern();

    [GeneratedRegex(@"\b(fiscal\s+year|FY)\s*'?(\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex FiscalYearPattern();

    [GeneratedRegex(@"\b(spring|summer|fall|autumn|winter)\s+(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();

    private static readonly Dictionary<string, DayOfWeek> DayOfWeekNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday
    };

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["february"] = 2, ["march"] = 3, ["april"] = 4,
        ["may"] = 5, ["june"] = 6, ["july"] = 7, ["august"] = 8,
        ["september"] = 9, ["october"] = 10, ["november"] = 11, ["december"] = 12
    };

    public HybridDateExtractionService(IDateExtractionService regexService)
    {
        _regexService = regexService;
    }

    /// <summary>
    /// Extract all dates including absolute and relative dates.
    /// </summary>
    public List<HybridExtractedDate> ExtractAllDates(string text, LocaleHint? localeHint = null, DateTimeOffset? referenceDate = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new List<HybridExtractedDate>();
        var seenSpans = new HashSet<(int Start, int End)>();
        var refDate = referenceDate ?? DateTimeOffset.UtcNow;

        // 1. Extract relative dates first (these are more specific)
        ExtractRelativeDates(text, refDate, results, seenSpans);

        // 2. Extract absolute dates using regex service
        var absoluteDates = _regexService.ExtractDates(text, localeHint);
        foreach (var absDate in absoluteDates)
        {
            var span = (absDate.StartIndex, absDate.EndIndex);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            seenSpans.Add(span);
            results.Add(new HybridExtractedDate
            {
                ParsedDate = absDate.ParsedDate,
                Format = absDate.Format,
                Confidence = absDate.Confidence,
                IsAmbiguous = absDate.IsAmbiguous,
                IsRelative = false,
                OriginalText = absDate.OriginalText,
                StartIndex = absDate.StartIndex,
                EndIndex = absDate.EndIndex,
                DatePrecision = DeterminePrecision(absDate.Format),
                DetectionMethod = "regex"
            });
        }

        return results.OrderBy(r => r.StartIndex).ToList();
    }

    /// <summary>
    /// Combine NER-detected DATE spans with regex parsing for enhanced extraction.
    /// Call this with spans from NER model for hybrid approach.
    /// </summary>
    public List<HybridExtractedDate> ParseNerDateSpans(
        IEnumerable<(string Text, int StartOffset, double Confidence)> nerSpans,
        string fullText,
        LocaleHint? localeHint = null,
        DateTimeOffset? referenceDate = null)
    {
        var results = new List<HybridExtractedDate>();
        var refDate = referenceDate ?? DateTimeOffset.UtcNow;

        foreach (var span in nerSpans)
        {
            // Try regex parsing first
            var regexDates = _regexService.ExtractDates(span.Text, localeHint);
            if (regexDates.Count > 0)
            {
                var best = regexDates.OrderByDescending(d => d.Confidence).First();
                results.Add(new HybridExtractedDate
                {
                    ParsedDate = best.ParsedDate,
                    Format = best.Format,
                    Confidence = Math.Min(span.Confidence, best.Confidence), // Use lower confidence
                    IsAmbiguous = best.IsAmbiguous,
                    IsRelative = false,
                    OriginalText = span.Text,
                    StartIndex = span.StartOffset,
                    EndIndex = span.StartOffset + span.Text.Length,
                    DatePrecision = DeterminePrecision(best.Format),
                    DetectionMethod = "ner+regex"
                });
                continue;
            }

            // Try relative date parsing
            var relativeDate = ParseRelativeDate(span.Text, refDate);
            if (relativeDate != null)
            {
                results.Add(relativeDate with
                {
                    OriginalText = span.Text,
                    StartIndex = span.StartOffset,
                    EndIndex = span.StartOffset + span.Text.Length,
                    Confidence = Math.Min(span.Confidence, relativeDate.Confidence),
                    DetectionMethod = "ner+relative"
                });
                continue;
            }

            // NER detected a date but we couldn't parse it - store as unparsed
            results.Add(new HybridExtractedDate
            {
                ParsedDate = null,
                Format = "unknown",
                Confidence = span.Confidence * 0.5, // Lower confidence for unparsed
                IsAmbiguous = true,
                IsRelative = false,
                OriginalText = span.Text,
                StartIndex = span.StartOffset,
                EndIndex = span.StartOffset + span.Text.Length,
                DatePrecision = DatePrecision.Unknown,
                DetectionMethod = "ner_only"
            });
        }

        return results;
    }

    private void ExtractRelativeDates(
        string text,
        DateTimeOffset refDate,
        List<HybridExtractedDate> results,
        HashSet<(int Start, int End)> seenSpans)
    {
        // Simple day patterns: today, yesterday, tomorrow
        foreach (Match match in SimpleDayPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseSimpleDay(match.Value, refDate);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        // Relative weekday: last Monday, next Friday
        foreach (Match match in RelativeWeekdayPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseRelativeWeekday(match, refDate);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        // Relative periods: last week, this month, next year
        foreach (Match match in RelativePeriodPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseRelativePeriod(match, refDate);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        // Relative offset: 3 days ago, 2 weeks from now
        foreach (Match match in RelativeOffsetPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseRelativeOffset(match, refDate);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        // Quarters: Q1 2024, Q3 2023
        foreach (Match match in QuarterPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseQuarter(match);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        // Seasons: Spring 2024, Winter 2023
        foreach (Match match in SeasonPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseSeason(match);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        // Fiscal year: FY24, Fiscal Year 2024
        foreach (Match match in FiscalYearPattern().Matches(text))
        {
            var span = (match.Index, match.Index + match.Length);
            if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                continue;

            var date = ParseFiscalYear(match);
            if (date != null)
            {
                seenSpans.Add(span);
                results.Add(date with
                {
                    OriginalText = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }
    }

    private HybridExtractedDate? ParseRelativeDate(string text, DateTimeOffset refDate)
    {
        // Try simple day
        var simpleMatch = SimpleDayPattern().Match(text);
        if (simpleMatch.Success)
            return ParseSimpleDay(simpleMatch.Value, refDate);

        // Try relative weekday
        var weekdayMatch = RelativeWeekdayPattern().Match(text);
        if (weekdayMatch.Success)
            return ParseRelativeWeekday(weekdayMatch, refDate);

        // Try relative period
        var periodMatch = RelativePeriodPattern().Match(text);
        if (periodMatch.Success)
            return ParseRelativePeriod(periodMatch, refDate);

        // Try relative offset
        var offsetMatch = RelativeOffsetPattern().Match(text);
        if (offsetMatch.Success)
            return ParseRelativeOffset(offsetMatch, refDate);

        // Try quarter
        var quarterMatch = QuarterPattern().Match(text);
        if (quarterMatch.Success)
            return ParseQuarter(quarterMatch);

        // Try season
        var seasonMatch = SeasonPattern().Match(text);
        if (seasonMatch.Success)
            return ParseSeason(seasonMatch);

        return null;
    }

    private static HybridExtractedDate? ParseSimpleDay(string day, DateTimeOffset refDate)
    {
        var lower = day.ToLowerInvariant();
        var date = lower switch
        {
            "today" => refDate.Date,
            "yesterday" => refDate.Date.AddDays(-1),
            "tomorrow" => refDate.Date.AddDays(1),
            _ => (DateTime?)null
        };

        if (date == null) return null;

        return new HybridExtractedDate
        {
            ParsedDate = new DateTimeOffset(date.Value, TimeSpan.Zero),
            Format = "Relative",
            Confidence = 1.0,
            IsAmbiguous = false,
            IsRelative = true,
            DatePrecision = DatePrecision.Day,
            DetectionMethod = "regex_relative"
        };
    }

    private static HybridExtractedDate? ParseRelativeWeekday(Match match, DateTimeOffset refDate)
    {
        var direction = match.Groups[1].Value.ToLowerInvariant();
        var dayName = match.Groups[2].Value.ToLowerInvariant();

        if (!DayOfWeekNames.TryGetValue(dayName, out var targetDay))
            return null;

        var currentDay = refDate.DayOfWeek;
        var dayDiff = ((int)targetDay - (int)currentDay + 7) % 7;

        var date = direction switch
        {
            "last" => refDate.AddDays(dayDiff == 0 ? -7 : dayDiff - 7),
            "next" => refDate.AddDays(dayDiff == 0 ? 7 : dayDiff),
            "this" => refDate.AddDays(dayDiff),
            _ => (DateTimeOffset?)null
        };

        if (date == null) return null;

        return new HybridExtractedDate
        {
            ParsedDate = date,
            Format = "RelativeWeekday",
            Confidence = 0.95,
            IsAmbiguous = false,
            IsRelative = true,
            DatePrecision = DatePrecision.Day,
            DetectionMethod = "regex_relative"
        };
    }

    private static HybridExtractedDate? ParseRelativePeriod(Match match, DateTimeOffset refDate)
    {
        var direction = match.Groups[1].Value.ToLowerInvariant();
        var period = match.Groups[2].Value.ToLowerInvariant();

        var multiplier = direction switch
        {
            "last" => -1,
            "next" => 1,
            "this" => 0,
            _ => 0
        };

        DateTimeOffset startDate;
        DatePrecision precision;

        switch (period)
        {
            case "week":
                // Start of week (Monday)
                var daysToMonday = (int)refDate.DayOfWeek - 1;
                if (daysToMonday < 0) daysToMonday = 6;
                startDate = refDate.AddDays(-daysToMonday + (multiplier * 7));
                precision = DatePrecision.Week;
                break;

            case "month":
                startDate = new DateTimeOffset(refDate.Year, refDate.Month, 1, 0, 0, 0, TimeSpan.Zero)
                    .AddMonths(multiplier);
                precision = DatePrecision.Month;
                break;

            case "year":
                startDate = new DateTimeOffset(refDate.Year + multiplier, 1, 1, 0, 0, 0, TimeSpan.Zero);
                precision = DatePrecision.Year;
                break;

            case "quarter":
                var currentQuarter = (refDate.Month - 1) / 3;
                var targetQuarter = currentQuarter + multiplier;
                var targetYear = refDate.Year + (targetQuarter < 0 ? -1 : targetQuarter >= 4 ? 1 : 0);
                targetQuarter = ((targetQuarter % 4) + 4) % 4;
                startDate = new DateTimeOffset(targetYear, (targetQuarter * 3) + 1, 1, 0, 0, 0, TimeSpan.Zero);
                precision = DatePrecision.Quarter;
                break;

            default:
                return null;
        }

        return new HybridExtractedDate
        {
            ParsedDate = startDate,
            Format = "RelativePeriod",
            Confidence = 0.9,
            IsAmbiguous = false,
            IsRelative = true,
            DatePrecision = precision,
            DetectionMethod = "regex_relative"
        };
    }

    private static HybridExtractedDate? ParseRelativeOffset(Match match, DateTimeOffset refDate)
    {
        if (!int.TryParse(match.Groups[1].Value, out var amount))
            return null;

        var unit = match.Groups[2].Value.ToLowerInvariant().TrimEnd('s');
        var direction = match.Groups[3].Value.ToLowerInvariant();

        var multiplier = direction.Contains("ago") ? -1 : 1;

        var date = unit switch
        {
            "day" => refDate.AddDays(amount * multiplier),
            "week" => refDate.AddDays(amount * 7 * multiplier),
            "month" => refDate.AddMonths(amount * multiplier),
            "year" => refDate.AddYears(amount * multiplier),
            _ => (DateTimeOffset?)null
        };

        if (date == null) return null;

        return new HybridExtractedDate
        {
            ParsedDate = date,
            Format = "RelativeOffset",
            Confidence = 0.95,
            IsAmbiguous = false,
            IsRelative = true,
            DatePrecision = DatePrecision.Day,
            DetectionMethod = "regex_relative"
        };
    }

    private static HybridExtractedDate? ParseQuarter(Match match)
    {
        if (!int.TryParse(match.Groups[1].Value, out var quarter) ||
            !int.TryParse(match.Groups[2].Value, out var year))
            return null;

        if (quarter < 1 || quarter > 4) return null;

        var month = (quarter - 1) * 3 + 1;
        var date = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);

        return new HybridExtractedDate
        {
            ParsedDate = date,
            Format = "Quarter",
            Confidence = 0.95,
            IsAmbiguous = false,
            IsRelative = false,
            DatePrecision = DatePrecision.Quarter,
            DetectionMethod = "regex"
        };
    }

    private static HybridExtractedDate? ParseSeason(Match match)
    {
        var season = match.Groups[1].Value.ToLowerInvariant();
        if (!int.TryParse(match.Groups[2].Value, out var year))
            return null;

        // Northern hemisphere seasons (approximate starts)
        var (month, day) = season switch
        {
            "spring" => (3, 20),
            "summer" => (6, 21),
            "fall" or "autumn" => (9, 22),
            "winter" => (12, 21),
            _ => (0, 0)
        };

        if (month == 0) return null;

        var date = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);

        return new HybridExtractedDate
        {
            ParsedDate = date,
            Format = "Season",
            Confidence = 0.8, // Seasons are approximate
            IsAmbiguous = true, // Could be NH or SH
            IsRelative = false,
            DatePrecision = DatePrecision.Season,
            DetectionMethod = "regex"
        };
    }

    private static HybridExtractedDate? ParseFiscalYear(Match match)
    {
        var yearStr = match.Groups[2].Value;
        if (!int.TryParse(yearStr, out var year))
            return null;

        // Handle 2-digit years
        if (year < 100)
        {
            year += year < 50 ? 2000 : 1900;
        }

        // Fiscal year typically starts July 1 (US federal) or varies by company
        // Default to calendar year start for simplicity
        var date = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return new HybridExtractedDate
        {
            ParsedDate = date,
            Format = "FiscalYear",
            Confidence = 0.7, // Fiscal year start varies
            IsAmbiguous = true,
            IsRelative = false,
            DatePrecision = DatePrecision.Year,
            DetectionMethod = "regex"
        };
    }

    private static DatePrecision DeterminePrecision(string format)
    {
        return format switch
        {
            "ISO8601" or "MonthFirst" or "DayFirst" or "SlashNumeric" or "DotNumeric" or "DashNumeric"
                => DatePrecision.Day,
            "YearMonth" or "MonthYear" => DatePrecision.Month,
            "Quarter" => DatePrecision.Quarter,
            "Season" => DatePrecision.Season,
            "FiscalYear" => DatePrecision.Year,
            _ => DatePrecision.Unknown
        };
    }

    private static bool OverlapsSignificantly((int Start, int End) a, (int Start, int End) b)
    {
        var overlapStart = Math.Max(a.Start, b.Start);
        var overlapEnd = Math.Min(a.End, b.End);
        var overlap = Math.Max(0, overlapEnd - overlapStart);
        var minLength = Math.Min(a.End - a.Start, b.End - b.Start);
        return overlap > minLength * 0.5;
    }
}

/// <summary>
/// Interface for hybrid date extraction combining NER and regex approaches.
/// </summary>
public interface IHybridDateExtractionService
{
    /// <summary>
    /// Extract all dates (absolute and relative) from text.
    /// </summary>
    List<HybridExtractedDate> ExtractAllDates(string text, LocaleHint? localeHint = null, DateTimeOffset? referenceDate = null);

    /// <summary>
    /// Parse NER-detected date spans using regex patterns.
    /// </summary>
    List<HybridExtractedDate> ParseNerDateSpans(
        IEnumerable<(string Text, int StartOffset, double Confidence)> nerSpans,
        string fullText,
        LocaleHint? localeHint = null,
        DateTimeOffset? referenceDate = null);
}

/// <summary>
/// Extended date extraction result with precision and detection method info.
/// </summary>
public record HybridExtractedDate
{
    public DateTimeOffset? ParsedDate { get; init; }
    public required string Format { get; init; }
    public double Confidence { get; init; }
    public bool IsAmbiguous { get; init; }
    public bool IsRelative { get; init; }
    public string? OriginalText { get; init; }
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public DatePrecision DatePrecision { get; init; }
    public string DetectionMethod { get; init; } = "regex";
}

/// <summary>
/// Date precision level.
/// </summary>
public enum DatePrecision
{
    Unknown,
    Year,
    Quarter,
    Season,
    Month,
    Week,
    Day,
    Hour,
    Minute,
    Second
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace LucidRAG.Services;

/// <summary>
/// Extracts and normalizes dates from text with international format support.
/// Handles ambiguous formats (e.g., 01/02/2024) by detecting locale patterns.
/// </summary>
public partial class DateExtractionService : IDateExtractionService
{
    // Source-generated regex patterns for performance

    // ISO 8601: 2024-01-15, 2024-01-15T10:30:00Z
    [GeneratedRegex(@"\b(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})(?:T(?<time>\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:?\d{2})?))?\b")]
    private static partial Regex Iso8601Pattern();

    // Long format: January 15, 2024 / Jan 15, 2024
    [GeneratedRegex(@"\b(?<month>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s*(?<year>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthFirstPattern();

    // Long format: 15 January 2024
    [GeneratedRegex(@"\b(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\.?,?\s*(?<year>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayFirstPattern();

    // Numeric with slashes: 01/15/2024 (US) or 15/01/2024 (EU)
    [GeneratedRegex(@"\b(?<first>\d{1,2})/(?<second>\d{1,2})/(?<year>\d{4})\b")]
    private static partial Regex SlashNumericPattern();

    // Numeric with dots: 15.01.2024 (EU common)
    [GeneratedRegex(@"\b(?<day>\d{1,2})\.(?<month>\d{1,2})\.(?<year>\d{4})\b")]
    private static partial Regex DotNumericPattern();

    // Numeric with dashes (non-ISO): 15-01-2024 or 01-15-2024
    [GeneratedRegex(@"\b(?<first>\d{1,2})-(?<second>\d{1,2})-(?<year>\d{4})\b")]
    private static partial Regex DashNumericPattern();

    // Year-month only: 2024-01
    [GeneratedRegex(@"\b(?<year>\d{4})-(?<month>\d{2})\b")]
    private static partial Regex YearMonthPattern();

    // Month Year: January 2024
    [GeneratedRegex(@"\b(?<month>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\.?\s+(?<year>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthYearPattern();

    // Locale detection patterns
    [GeneratedRegex(@"\b(colour|favour|centre|organisation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BritishEnglishPattern();

    [GeneratedRegex(@"\b(color|favor|center|organization)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmericanEnglishPattern();

    // Pattern registry with metadata
    private static readonly (Func<Regex> Pattern, string Format, DateOrder Order)[] DatePatterns =
    [
        (Iso8601Pattern, "ISO8601", DateOrder.YMD),
        (MonthFirstPattern, "MonthFirst", DateOrder.MDY),
        (DayFirstPattern, "DayFirst", DateOrder.DMY),
        (SlashNumericPattern, "SlashNumeric", DateOrder.Ambiguous),
        (DotNumericPattern, "DotNumeric", DateOrder.DMY),
        (DashNumericPattern, "DashNumeric", DateOrder.Ambiguous),
        (YearMonthPattern, "YearMonth", DateOrder.YMD),
        (MonthYearPattern, "MonthYear", DateOrder.MDY),
    ];

    // Month name to number mapping
    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["jan"] = 1,
        ["february"] = 2, ["feb"] = 2,
        ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["may"] = 5,
        ["june"] = 6, ["jun"] = 6,
        ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9,
        ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12
    };

    /// <summary>
    /// Extract all dates from text with confidence scores.
    /// </summary>
    public List<ExtractedDate> ExtractDates(string text, LocaleHint? localeHint = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new List<ExtractedDate>();
        var seenSpans = new HashSet<(int Start, int End)>();

        foreach (var (patternFactory, format, order) in DatePatterns)
        {
            var pattern = patternFactory();
            foreach (Match match in pattern.Matches(text))
            {
                var span = (match.Index, match.Index + match.Length);
                if (seenSpans.Any(s => OverlapsSignificantly(s, span)))
                    continue;

                var extracted = ParseMatch(match, format, order, localeHint);
                if (extracted != null)
                {
                    seenSpans.Add(span);
                    results.Add(extracted with
                    {
                        OriginalText = match.Value,
                        StartIndex = match.Index,
                        EndIndex = match.Index + match.Length
                    });
                }
            }
        }

        // Sort by position in text
        return results.OrderBy(r => r.StartIndex).ToList();
    }

    /// <summary>
    /// Detect the predominant date locale in a document by analyzing all dates.
    /// Returns DMY (European) or MDY (US) based on evidence.
    /// </summary>
    public LocaleHint DetectLocale(string text)
    {
        var dmyEvidence = 0;
        var mdyEvidence = 0;

        // Look for unambiguous patterns first
        foreach (var (patternFactory, _, order) in DatePatterns)
        {
            var pattern = patternFactory();
            if (order == DateOrder.DMY)
            {
                dmyEvidence += pattern.Matches(text).Count * 2; // Strong evidence
            }
            else if (order == DateOrder.MDY)
            {
                mdyEvidence += pattern.Matches(text).Count * 2;
            }
        }

        // Analyze ambiguous dates (slash format) for impossible values
        var slashPattern = SlashNumericPattern();
        foreach (Match match in slashPattern.Matches(text))
        {
            var first = int.Parse(match.Groups["first"].Value);
            var second = int.Parse(match.Groups["second"].Value);

            // If first > 12, must be day (DMY format)
            if (first > 12 && second <= 12)
                dmyEvidence += 3;
            // If second > 12, must be day (MDY format)
            else if (second > 12 && first <= 12)
                mdyEvidence += 3;
        }

        // Check for locale-specific keywords
        if (BritishEnglishPattern().IsMatch(text))
            dmyEvidence += 1; // British English
        if (AmericanEnglishPattern().IsMatch(text))
            mdyEvidence += 1; // American English

        return dmyEvidence > mdyEvidence ? LocaleHint.European : LocaleHint.American;
    }

    /// <summary>
    /// Parse a date string with explicit format.
    /// </summary>
    public DateTimeOffset? ParseDate(string dateString, LocaleHint? localeHint = null)
    {
        var dates = ExtractDates(dateString, localeHint);
        return dates.FirstOrDefault()?.ParsedDate;
    }

    /// <summary>
    /// Normalize all dates in text to ISO 8601 format.
    /// </summary>
    public string NormalizeDatesInText(string text, LocaleHint? localeHint = null)
    {
        localeHint ??= DetectLocale(text);
        var dates = ExtractDates(text, localeHint);

        // Replace from end to preserve indices
        var result = text;
        foreach (var date in dates.OrderByDescending(d => d.StartIndex))
        {
            if (date.ParsedDate.HasValue)
            {
                var iso = date.ParsedDate.Value.ToString("yyyy-MM-dd");
                result = result[..date.StartIndex] + iso + result[date.EndIndex..];
            }
        }

        return result;
    }

    private ExtractedDate? ParseMatch(Match match, string format, DateOrder order, LocaleHint? localeHint)
    {
        try
        {
            int year, month, day;
            double confidence = 1.0;

            switch (format)
            {
                case "ISO8601":
                    year = int.Parse(match.Groups["year"].Value);
                    month = int.Parse(match.Groups["month"].Value);
                    day = int.Parse(match.Groups["day"].Value);
                    confidence = 1.0; // ISO is unambiguous
                    break;

                case "MonthFirst":
                case "DayFirst":
                    year = int.Parse(match.Groups["year"].Value);
                    month = MonthNames[match.Groups["month"].Value.TrimEnd('.')];
                    day = int.Parse(match.Groups["day"].Value);
                    confidence = 0.95; // Month name is unambiguous
                    break;

                case "SlashNumeric":
                case "DashNumeric":
                    year = int.Parse(match.Groups["year"].Value);
                    var first = int.Parse(match.Groups["first"].Value);
                    var second = int.Parse(match.Groups["second"].Value);

                    // Resolve ambiguity
                    if (first > 12)
                    {
                        // First must be day (DMY)
                        day = first;
                        month = second;
                        confidence = 0.95;
                    }
                    else if (second > 12)
                    {
                        // Second must be day (MDY)
                        month = first;
                        day = second;
                        confidence = 0.95;
                    }
                    else
                    {
                        // Truly ambiguous - use locale hint
                        var locale = localeHint ?? LocaleHint.American;
                        if (locale == LocaleHint.European)
                        {
                            day = first;
                            month = second;
                        }
                        else
                        {
                            month = first;
                            day = second;
                        }
                        confidence = 0.6; // Ambiguous
                    }
                    break;

                case "DotNumeric":
                    year = int.Parse(match.Groups["year"].Value);
                    day = int.Parse(match.Groups["day"].Value);
                    month = int.Parse(match.Groups["month"].Value);
                    confidence = 0.85; // Dot format is usually DMY
                    break;

                case "YearMonth":
                    year = int.Parse(match.Groups["year"].Value);
                    month = int.Parse(match.Groups["month"].Value);
                    day = 1; // Default to first of month
                    confidence = 0.9;
                    break;

                case "MonthYear":
                    year = int.Parse(match.Groups["year"].Value);
                    month = MonthNames[match.Groups["month"].Value.TrimEnd('.')];
                    day = 1;
                    confidence = 0.9;
                    break;

                default:
                    return null;
            }

            // Validate ranges
            if (month < 1 || month > 12 || day < 1 || day > 31)
                return null;

            // Check day is valid for month
            var daysInMonth = DateTime.DaysInMonth(year, month);
            if (day > daysInMonth)
                return null;

            var parsedDate = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);

            return new ExtractedDate
            {
                ParsedDate = parsedDate,
                Format = format,
                Confidence = confidence,
                IsAmbiguous = order == DateOrder.Ambiguous && confidence < 0.9
            };
        }
        catch
        {
            return null;
        }
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

public interface IDateExtractionService
{
    List<ExtractedDate> ExtractDates(string text, LocaleHint? localeHint = null);
    LocaleHint DetectLocale(string text);
    DateTimeOffset? ParseDate(string dateString, LocaleHint? localeHint = null);
    string NormalizeDatesInText(string text, LocaleHint? localeHint = null);
}

public record ExtractedDate
{
    public DateTimeOffset? ParsedDate { get; init; }
    public required string Format { get; init; }
    public double Confidence { get; init; }
    public bool IsAmbiguous { get; init; }
    public string? OriginalText { get; init; }
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
}

public enum LocaleHint
{
    American,  // MM/DD/YYYY
    European,  // DD/MM/YYYY
    ISO        // YYYY-MM-DD (always unambiguous)
}

public enum DateOrder
{
    YMD,       // Year-Month-Day (ISO)
    MDY,       // Month-Day-Year (US)
    DMY,       // Day-Month-Year (EU)
    Ambiguous  // Could be MDY or DMY
}

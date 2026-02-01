using System.Text.RegularExpressions;

namespace DoomSummarizer.Plugins.Subtitles;

/// <summary>
/// Detected chapter boundary within a subtitle stream.
/// </summary>
public record SubtitleChapter(
    TimeSpan StartTime,
    string Title,
    int StartIndex);

/// <summary>
/// Detects chapter-like boundaries in subtitle streams using gap analysis
/// and marker detection (e.g., [Music], [Applause], all-caps titles).
/// </summary>
public static partial class ChapterDetector
{
    private static readonly HashSet<string> SectionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "[music]", "[applause]", "[laughter]", "[cheering]",
        "[silence]", "[inaudible]", "[music playing]",
        "♪", "♫"
    };

    /// <summary>
    /// Detect chapter boundaries from subtitle entries using gap and marker heuristics.
    /// </summary>
    /// <param name="entries">Ordered subtitle entries with timing info.</param>
    /// <param name="gapThresholdSeconds">Minimum gap between entries to mark a chapter boundary.</param>
    /// <returns>List of detected chapter boundaries.</returns>
    public static List<SubtitleChapter> DetectChapters(
        IReadOnlyList<SubtitleEntry> entries,
        double gapThresholdSeconds = 5.0)
    {
        if (entries.Count == 0)
            return [];

        var chapters = new List<SubtitleChapter>();
        var gapThreshold = TimeSpan.FromSeconds(gapThresholdSeconds);

        // First entry always starts a chapter
        chapters.Add(new SubtitleChapter(
            entries[0].StartTime,
            InferChapterTitle(entries, 0),
            0));

        for (var i = 1; i < entries.Count; i++)
        {
            var prev = entries[i - 1];
            var curr = entries[i];
            var gap = curr.StartTime - prev.EndTime;

            var isGapBoundary = gap >= gapThreshold;
            var isMarkerBoundary = IsSectionMarker(prev.Text);
            var isTitleBoundary = IsLikelyTitle(curr.Text);

            if (isGapBoundary || isMarkerBoundary || isTitleBoundary)
            {
                var title = isTitleBoundary
                    ? curr.Text.Trim()
                    : InferChapterTitle(entries, i);

                chapters.Add(new SubtitleChapter(curr.StartTime, title, i));
            }
        }

        return chapters;
    }

    private static bool IsSectionMarker(string text)
    {
        var trimmed = text.Trim();
        return SectionMarkers.Any(m => trimmed.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyTitle(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        // All-caps and short (< 10 words) suggests a title/heading
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 10 || words.Length == 0) return false;

        // Check if all alphabetic characters are uppercase
        var alphaChars = trimmed.Where(char.IsLetter).ToArray();
        return alphaChars.Length > 2 && alphaChars.All(char.IsUpper);
    }

    private static string InferChapterTitle(IReadOnlyList<SubtitleEntry> entries, int startIndex)
    {
        // Use the first meaningful text after the boundary as the title hint
        for (var i = startIndex; i < Math.Min(startIndex + 3, entries.Count); i++)
        {
            var text = entries[i].Text.Trim();
            if (!string.IsNullOrEmpty(text) && !IsSectionMarker(text))
            {
                // Truncate long text to use as a title
                return text.Length > 60 ? text[..57] + "..." : text;
            }
        }

        return $"Section at {entries[startIndex].StartTime:hh\\:mm\\:ss}";
    }

    /// <summary>
    /// Format a TimeSpan as a timestamp string (HH:MM:SS).
    /// </summary>
    public static string FormatTimestamp(TimeSpan ts)
        => ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
}

/// <summary>
/// Normalized subtitle entry with timing and cleaned text.
/// </summary>
public record SubtitleEntry(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text);

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
public static class ChapterDetector
{
    // Exact-match markers (checked via HashSet.Contains — O(1))
    private static readonly HashSet<string> ExactMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "[music]", "[applause]", "[laughter]", "[cheering]",
        "[silence]", "[inaudible]", "[music playing]",
        "♪", "♫"
    };

    // Substring markers for Contains check (only checked when exact match fails)
    private static readonly string[] SubstringMarkers = ["[music", "[applause", "[laughter", "♪", "♫"];

    /// <summary>
    /// Detect chapter boundaries from subtitle entries using gap and marker heuristics.
    /// </summary>
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
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty) return false;

        // Fast path: exact match against HashSet (O(1) lookup, no allocation)
        if (ExactMarkers.Contains(text.Trim()))
            return true;

        // Slow path: substring match for partial markers like "[Music] and talking"
        foreach (var marker in SubstringMarkers)
        {
            if (trimmed.Contains(marker.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLikelyTitle(string text)
    {
        var span = text.AsSpan().Trim();
        if (span.IsEmpty) return false;

        // Count words and check length — no allocation (span-based)
        var wordCount = 0;
        var inWord = false;
        var letterCount = 0;
        var upperCount = 0;

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];

            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                wordCount++;
                if (wordCount > 10) return false; // Early exit
            }

            if (char.IsLetter(ch))
            {
                letterCount++;
                if (char.IsUpper(ch)) upperCount++;
            }
        }

        // All-caps: at least 3 letters and all uppercase
        return letterCount > 2 && letterCount == upperCount;
    }

    private static string InferChapterTitle(IReadOnlyList<SubtitleEntry> entries, int startIndex)
    {
        var end = Math.Min(startIndex + 3, entries.Count);
        for (var i = startIndex; i < end; i++)
        {
            var text = entries[i].Text.Trim();
            if (!string.IsNullOrEmpty(text) && !IsSectionMarker(text))
            {
                return text.Length > 60 ? string.Concat(text.AsSpan(0, 57), "...") : text;
            }
        }

        return $"Section at {entries[startIndex].StartTime:hh\\:mm\\:ss}";
    }

    /// <summary>
    /// Format a TimeSpan as a timestamp string (H:MM:SS or M:SS).
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

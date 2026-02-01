using System.Text.RegularExpressions;

namespace DoomSummarizer.Sources.YouTube;

/// <summary>
/// Parsed chapter from a YouTube video description timestamp.
/// </summary>
public record DescriptionChapter(
    TimeSpan Timestamp,
    string Title);

/// <summary>
/// Parses timestamp-based chapter markers from YouTube video descriptions.
/// YouTube descriptions commonly include timestamps like:
///   0:00 Introduction
///   2:30 Setting up the project
///   5:15 Demo
///   10:45 Q&amp;A
/// </summary>
public static partial class DescriptionChapterParser
{
    // Matches lines like "0:00 Introduction" or "1:23:45 Final section"
    [GeneratedRegex(@"^(\d{1,2}:\d{2}(?::\d{2})?)\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex TimestampLineRegex();

    /// <summary>
    /// Parse chapter timestamps from a YouTube video description.
    /// </summary>
    /// <param name="description">The video description text.</param>
    /// <returns>Ordered list of chapters, or empty if no timestamps found.</returns>
    public static List<DescriptionChapter> Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return [];

        var chapters = new List<DescriptionChapter>();
        var matches = TimestampLineRegex().Matches(description);

        foreach (Match match in matches)
        {
            if (TryParseTimestamp(match.Groups[1].Value, out var ts))
            {
                var title = match.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(title))
                    chapters.Add(new DescriptionChapter(ts, title));
            }
        }

        // Only return if we found at least 2 timestamps (single timestamp is likely not chapters)
        if (chapters.Count < 2)
            return [];

        // Ensure they're ordered by timestamp
        chapters.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        return chapters;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var parts = value.Split(':');

        try
        {
            if (parts.Length == 2)
            {
                // M:SS or MM:SS
                var minutes = int.Parse(parts[0]);
                var seconds = int.Parse(parts[1]);
                result = new TimeSpan(0, minutes, seconds);
                return true;
            }

            if (parts.Length == 3)
            {
                // H:MM:SS
                var hours = int.Parse(parts[0]);
                var minutes = int.Parse(parts[1]);
                var seconds = int.Parse(parts[2]);
                result = new TimeSpan(hours, minutes, seconds);
                return true;
            }
        }
        catch (Exception) when (parts.Length > 0)
        {
            // Invalid number format or overflow
        }

        return false;
    }
}

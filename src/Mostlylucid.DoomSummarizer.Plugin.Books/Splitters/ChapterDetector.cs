using System.Text.RegularExpressions;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Splitters;

/// <summary>
///     A detected boundary in the document text.
/// </summary>
public record DetectedBoundary
{
    /// <summary>Line index in the source text where this boundary was found.</summary>
    public required int LineIndex { get; init; }

    /// <summary>Character offset in the source text.</summary>
    public required int CharOffset { get; init; }

    /// <summary>The matched heading/marker text.</summary>
    public required string MarkerText { get; init; }

    /// <summary>Level label (e.g. "chapter", "act", "section").</summary>
    public required string Level { get; init; }

    /// <summary>Sequence number extracted from the marker (e.g. 1, 2, 3), or -1 if not numbered.</summary>
    public int SequenceNumber { get; init; } = -1;
}

/// <summary>
///     Detects chapter, act, section, and other structural boundaries in text
///     using regex patterns from structural pattern YAML files.
/// </summary>
public static class ChapterDetector
{
    private static readonly Dictionary<char, int> RomanNumerals = new()
    {
        ['I'] = 1, ['V'] = 5, ['X'] = 10,
        ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000
    };

    /// <summary>
    ///     Find all boundaries for a given level using the specified pattern.
    /// </summary>
    public static List<DetectedBoundary> DetectBoundaries(
        string text,
        StructuralPattern pattern,
        string level)
    {
        if (!pattern.MarkersByLevel.TryGetValue(level, out var regexes))
            return [];

        var boundaries = new List<DetectedBoundary>();
        var lines = text.Split('\n');
        var charOffset = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            foreach (var regex in regexes)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    var seqNum = ExtractSequenceNumber(match);
                    boundaries.Add(new DetectedBoundary
                    {
                        LineIndex = i,
                        CharOffset = charOffset,
                        MarkerText = line.Trim(),
                        Level = level,
                        SequenceNumber = seqNum
                    });
                    break; // Only match once per line
                }
            }

            charOffset += lines[i].Length + 1; // +1 for newline
        }

        return boundaries;
    }

    /// <summary>
    ///     Detect all boundaries at all levels for a given pattern.
    ///     Returns boundaries sorted by character offset.
    /// </summary>
    public static List<DetectedBoundary> DetectAllBoundaries(string text, StructuralPattern pattern)
    {
        var all = new List<DetectedBoundary>();

        foreach (var level in pattern.MarkersByLevel.Keys) all.AddRange(DetectBoundaries(text, pattern, level));

        return all.OrderBy(b => b.CharOffset).ToList();
    }

    /// <summary>
    ///     Try to extract a sequence number from a regex match.
    ///     Handles: Arabic (1, 2, 3), Roman (I, II, III), English (One, Two, Three).
    /// </summary>
    private static int ExtractSequenceNumber(Match match)
    {
        // Check capture groups
        for (var g = 1; g < match.Groups.Count; g++)
        {
            var value = match.Groups[g].Value.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            // Try Arabic numerals
            if (int.TryParse(value, out var arabic))
                return arabic;

            // Try Roman numerals
            var roman = TryParseRoman(value);
            if (roman > 0) return roman;

            // Try English number words
            var english = TryParseEnglishNumber(value);
            if (english > 0) return english;
        }

        return -1;
    }

    private static int TryParseRoman(string text)
    {
        var upper = text.ToUpperInvariant();
        var values = RomanNumerals;

        if (upper.Any(c => !values.ContainsKey(c)))
            return 0;

        var result = 0;
        for (var i = 0; i < upper.Length; i++)
        {
            var current = values[upper[i]];
            var next = i + 1 < upper.Length ? values[upper[i + 1]] : 0;

            if (current < next)
                result -= current;
            else
                result += current;
        }

        return result;
    }

    private static int TryParseEnglishNumber(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
            "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
            "eleven" => 11, "twelve" => 12, "thirteen" => 13, "fourteen" => 14,
            "fifteen" => 15, "sixteen" => 16, "seventeen" => 17, "eighteen" => 18,
            "nineteen" => 19, "twenty" => 20,
            _ => 0
        };
    }
}
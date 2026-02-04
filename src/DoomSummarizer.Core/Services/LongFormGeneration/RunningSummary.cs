using System.Text;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
///     Deterministic running summary accumulator.
///     Instead of LLM-compressing each section, carries forward the top-salience
///     evidence segment texts from completed sections. This gives each new section
///     awareness of what was covered without context stuffing.
/// </summary>
public class RunningSummary
{
    private const int MaxTotalChars = 1400;
    private readonly List<SectionDigest> _digests = [];

    /// <summary>
    ///     Record a completed section. Extracts the top-salience segment
    ///     from the section's assigned evidence as the digest.
    /// </summary>
    public void RecordSection(PlannedSection section, int sectionIndex)
    {
        // Pick the highest-salience segment from this section's evidence
        var topSegment = section.AssignedEvidence
            .OrderByDescending(e => e.Segment.SalienceScore)
            .FirstOrDefault();

        var digest = topSegment != null
            ? TruncateToSentence(topSegment.Segment.Text, 200)
            : ExtractFirstSentence(section.GeneratedContent ?? "", 150);

        _digests.Add(new SectionDigest
        {
            Heading = section.Heading,
            Digest = digest,
            SectionIndex = sectionIndex
        });
    }

    /// <summary>
    ///     Build the running summary string for the next section's context.
    ///     Two-tier compression:
    ///     Recent 2 sections: full digest (2-3 sentences from top-salience evidence)
    ///     Older sections: heading + first sentence only
    /// </summary>
    public string Build()
    {
        if (_digests.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("DOCUMENT SO FAR:");

        var totalChars = 0;
        // Recent 2 get full digest, older get truncated
        for (var i = 0; i < _digests.Count; i++)
        {
            var d = _digests[i];
            var isRecent = i >= _digests.Count - 2;

            string line;
            if (isRecent)
            {
                line = $"[{d.Heading}]: {d.Digest}";
            }
            else
            {
                // Older: just heading + first sentence
                var firstSentence = ExtractFirstSentence(d.Digest, 80);
                line = $"- [{d.Heading}]: {firstSentence}";
            }

            if (totalChars + line.Length > MaxTotalChars && i > 0)
                break; // Budget exhausted

            sb.AppendLine(line);
            totalChars += line.Length;
        }

        return sb.ToString();
    }

    private static string TruncateToSentence(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        // Find the last sentence boundary within maxChars
        var truncated = text[..maxChars];
        var lastPeriod = truncated.LastIndexOf('.');
        if (lastPeriod > maxChars / 2)
            return truncated[..(lastPeriod + 1)];
        return truncated + "...";
    }

    private static string ExtractFirstSentence(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var firstPeriod = text.IndexOf('.');
        if (firstPeriod > 0 && firstPeriod < maxChars)
            return text[..(firstPeriod + 1)];
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }

    private class SectionDigest
    {
        public string Heading { get; init; } = "";
        public string Digest { get; init; } = "";
        public int SectionIndex { get; init; }
    }
}
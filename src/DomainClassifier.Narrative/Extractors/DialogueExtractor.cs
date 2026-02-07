using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Narrative.Extractors;

/// <summary>
///     Extracts dialogue passages from narrative text.
///     Identifies quoted speech, speaker attribution, and computes dialogue density.
/// </summary>
public static partial class DialogueExtractor
{
    // Double-quoted dialogue
    [GeneratedRegex(@"""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex DoubleQuoteRegex();

    // Single-quoted dialogue (British style, min 10 chars to avoid contractions)
    [GeneratedRegex(@"'([^']{10,})'", RegexOptions.Compiled)]
    private static partial Regex SingleQuoteRegex();

    // Dialogue with speaker attribution: "text" said Name / "text," Name said
    [GeneratedRegex(
        @"""([^""]+)""\s*(?:,\s*)?(?:said|replied|exclaimed|whispered|murmured|shouted|cried|asked|answered|demanded|declared|remarked|observed)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)",
        RegexOptions.Compiled)]
    private static partial Regex AttributedDialogueRegex();

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();

        // Attributed dialogue (highest confidence - we know who's speaking)
        foreach (Match match in AttributedDialogueRegex().Matches(text))
        {
            var dialogue = match.Groups[1].Value;
            var speaker = match.Groups[2].Value;

            entities.Add(new DomainEntity(
                Text: dialogue,
                EntityType: "dialogue",
                DomainId: "narrative",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.95,
                Metadata: new Dictionary<string, object?>
                {
                    ["speaker"] = speaker,
                    ["attributed"] = true
                }));
        }

        // Track offsets already covered by attributed dialogue
        var coveredRanges = entities
            .Select(e => (e.StartOffset, e.EndOffset))
            .ToList();

        // Double-quoted dialogue (unattributed)
        foreach (Match match in DoubleQuoteRegex().Matches(text))
        {
            // Skip if already covered by attributed pattern
            if (coveredRanges.Any(r => match.Index >= r.StartOffset && match.Index < r.EndOffset))
                continue;

            var dialogue = match.Groups[1].Value;
            if (dialogue.Length < 3) continue; // skip very short quotes

            entities.Add(new DomainEntity(
                Text: dialogue,
                EntityType: "dialogue",
                DomainId: "narrative",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.80,
                Metadata: new Dictionary<string, object?>
                {
                    ["attributed"] = false
                }));
        }

        // Single-quoted dialogue (British style)
        foreach (Match match in SingleQuoteRegex().Matches(text))
        {
            if (coveredRanges.Any(r => match.Index >= r.StartOffset && match.Index < r.EndOffset))
                continue;

            var dialogue = match.Groups[1].Value;

            entities.Add(new DomainEntity(
                Text: dialogue,
                EntityType: "dialogue",
                DomainId: "narrative",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.70,
                Metadata: new Dictionary<string, object?>
                {
                    ["attributed"] = false,
                    ["quoteStyle"] = "single"
                }));
        }

        return entities;
    }

    /// <summary>
    ///     Compute dialogue density as ratio of dialogue lines to total lines.
    /// </summary>
    public static double ComputeDialogueDensity(string text)
    {
        return NarrativeKeywordClassifier.ComputeDialogueDensity(text);
    }
}

using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Narrative.Extractors;

/// <summary>
///     Extracts dialogue passages from narrative text.
///     Identifies quoted speech, speaker attribution, and computes dialogue density.
/// </summary>
public static partial class DialogueExtractor
{
    // Double-quoted dialogue (ASCII)
    [GeneratedRegex(@"""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex DoubleQuoteRegex();

    // Double-quoted dialogue (Unicode curly quotes \u201C...\u201D)
    [GeneratedRegex(@"\u201C([^\u201D]+)\u201D", RegexOptions.Compiled)]
    private static partial Regex CurlyDoubleQuoteRegex();

    // Single-quoted dialogue (British style, min 10 chars to avoid contractions)
    [GeneratedRegex(@"'([^']{10,})'", RegexOptions.Compiled)]
    private static partial Regex SingleQuoteRegex();

    // Dialogue with speaker attribution: "text" said Name (ASCII quotes)
    [GeneratedRegex(
        @"""([^""]+)""\s*(?:,\s*)?(?:said|replied|exclaimed|whispered|murmured|shouted|cried|asked|answered|demanded|declared|remarked|observed)\s+([A-Z][a-z]+(?:[ \t]+[A-Z][a-z]+)?)",
        RegexOptions.Compiled)]
    private static partial Regex AttributedDialogueRegex();

    // Dialogue with speaker attribution (Unicode curly quotes)
    [GeneratedRegex(
        @"\u201C([^\u201D]+)\u201D\s*(?:,\s*)?(?:said|replied|exclaimed|whispered|murmured|shouted|cried|asked|answered|demanded|declared|remarked|observed)\s+([A-Z][a-z]+(?:[ \t]+[A-Z][a-z]+)?)",
        RegexOptions.Compiled)]
    private static partial Regex CurlyAttributedDialogueRegex();

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();

        // Attributed dialogue — both ASCII and Unicode curly quotes
        foreach (var regex in new[] { AttributedDialogueRegex(), CurlyAttributedDialogueRegex() })
        {
            foreach (Match match in regex.Matches(text))
            {
                var dialogue = match.Groups[1].Value;
                var speaker = match.Groups[2].Value.Trim();

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
        }

        // Track offsets already covered by attributed dialogue
        var coveredRanges = entities
            .Select(e => (e.StartOffset, e.EndOffset))
            .ToList();

        // Double-quoted dialogue (unattributed) — ASCII and Unicode
        foreach (var regex in new[] { DoubleQuoteRegex(), CurlyDoubleQuoteRegex() })
        {
            foreach (Match match in regex.Matches(text))
            {
                if (coveredRanges.Any(r => match.Index >= r.StartOffset && match.Index < r.EndOffset))
                    continue;

                var dialogue = match.Groups[1].Value;
                if (dialogue.Length < 3) continue;

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

using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Narrative;

/// <summary>
///     Fast keyword + structural classifier for narrative/fiction content.
///     No model needed - uses weighted keyword matching plus dialogue density and chapter detection.
/// </summary>
public static partial class NarrativeKeywordClassifier
{
    // High-confidence narrative terms (weighted 3x)
    private static readonly HashSet<string> StrongTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "chapter", "protagonist", "antagonist", "narrator", "dialogue", "soliloquy",
        "monologue", "foreshadowing", "flashback", "climax", "denouement", "epilogue",
        "prologue", "novel", "novella", "once upon a time", "first person", "third person",
        "omniscient", "fiction", "narrative", "story arc", "plot twist", "character development"
    };

    // Medium-confidence narrative terms (weighted 1x)
    private static readonly HashSet<string> ModerateTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        // Speech verbs
        "said", "replied", "whispered", "exclaimed", "murmured", "shouted",
        "cried", "sighed", "gasped", "stammered", "muttered",
        // Structure
        "scene", "story", "tale", "adventure", "journey",
        // Settings
        "castle", "kingdom", "village", "forest", "mansion", "cottage",
        // Characters
        "hero", "heroine", "villain", "companion", "stranger",
        // Mood
        "love", "hatred", "fear", "courage", "betrayal", "destiny", "fate"
    };

    // Chapter/act heading patterns
    [GeneratedRegex(@"(?m)^(?:Chapter\s+\d+|CHAPTER\s+[IVXLCDM\d]+|Part\s+\d+|Act\s+\d+|Scene\s+\d+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ChapterHeadingRegex();

    /// <summary>
    ///     Classify text as narrative content based on keyword density plus structural signals.
    ///     Returns confidence from 0.0 to 1.0.
    /// </summary>
    public static DomainClassification Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DomainClassification("narrative", "Narrative Domain", 0.0);

        var textLower = text.ToLowerInvariant();
        var strongHits = 0;
        var moderateHits = 0;

        // Check multi-word terms in full text
        foreach (var term in StrongTerms)
            if (textLower.Contains(term.ToLowerInvariant()))
                strongHits++;

        foreach (var term in ModerateTerms)
            if (textLower.Contains(term.ToLowerInvariant()))
                moderateHits++;

        // Term-count scoring: independent of text length
        // 15 weighted term-hits = full confidence (e.g. 3 strong + 6 moderate)
        var weightedScore = strongHits * 3.0 + moderateHits;
        var confidence = Math.Min(1.0, weightedScore / 15.0);

        // Structural signals: dialogue density
        var dialogueDensity = ComputeDialogueDensity(text);
        if (dialogueDensity > 0.15)
            confidence += 0.15;

        // Structural signals: chapter headings
        var chapterMatches = ChapterHeadingRegex().Matches(text).Count;
        if (chapterMatches > 0)
            confidence += Math.Min(0.3, chapterMatches * 0.1);

        confidence = Math.Min(1.0, confidence);

        // Floor: require 2+ strong terms OR dialogue density > 0.15 for confidence > 0.4
        if (strongHits < 2 && dialogueDensity <= 0.15)
            confidence = Math.Min(confidence, 0.4);

        return new DomainClassification(
            "narrative",
            "Narrative Domain",
            Math.Round(confidence, 4));
    }

    /// <summary>
    ///     Compute dialogue density as the ratio of text blocks containing quoted speech.
    ///     Handles both ASCII quotes (") and Unicode curly quotes (\u201C \u201D).
    ///     Uses paragraph-based detection for wrapped text, falls back to line-based for short text.
    /// </summary>
    public static double ComputeDialogueDensity(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        // Try paragraph-based first (blank-line separated) for wrapped text
        var paragraphs = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        // If we have real paragraphs (>1), use paragraph-based density
        if (paragraphs.Length > 1)
        {
            var dialogueParagraphs = 0;
            foreach (var para in paragraphs)
            {
                if (ContainsQuotedDialogue(para))
                    dialogueParagraphs++;
            }

            return (double)dialogueParagraphs / paragraphs.Length;
        }

        // Fall back to line-based for text without paragraph breaks
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return 0;

        var dialogueLines = 0;
        foreach (var line in lines)
        {
            if (ContainsQuotedDialogue(line))
                dialogueLines++;
        }

        return (double)dialogueLines / lines.Length;
    }

    /// <summary>
    ///     Check if text contains quoted dialogue (ASCII or Unicode quotes).
    /// </summary>
    internal static bool ContainsQuotedDialogue(string text)
    {
        // ASCII double quotes: two " on same text block
        if (text.Contains('"') && text.IndexOf('"') != text.LastIndexOf('"'))
            return true;

        // Unicode curly double quotes: \u201C...\u201D
        if (text.Contains('\u201C') && text.Contains('\u201D'))
            return true;

        // Unicode single quotes (British style, min 10 chars between)
        var firstSingle = text.IndexOf('\u2018');
        var lastSingle = text.LastIndexOf('\u2019');
        if (firstSingle >= 0 && lastSingle > firstSingle + 10)
            return true;

        // ASCII single quotes with substantial content
        if (text.Contains('\''))
        {
            var first = text.IndexOf('\'');
            var last = text.LastIndexOf('\'');
            if (last > first + 10)
                return true;
        }

        return false;
    }
}

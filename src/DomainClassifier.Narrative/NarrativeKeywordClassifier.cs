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
        var words = textLower.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}'],
            StringSplitOptions.RemoveEmptyEntries);

        var totalWords = Math.Max(words.Length, 1);
        var strongHits = 0;
        var moderateHits = 0;

        // Check multi-word terms in full text
        foreach (var term in StrongTerms)
            if (textLower.Contains(term.ToLowerInvariant()))
                strongHits++;

        foreach (var term in ModerateTerms)
            if (textLower.Contains(term.ToLowerInvariant()))
                moderateHits++;

        // Weighted score: strong terms count 3x
        var weightedScore = (strongHits * 3.0 + moderateHits) / totalWords;

        // Normalize to 0-1 range
        var confidence = Math.Min(1.0, weightedScore * 10.0);

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
    ///     Compute the ratio of lines containing quoted dialogue to total lines.
    /// </summary>
    public static double ComputeDialogueDensity(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return 0;

        var dialogueLines = 0;
        foreach (var line in lines)
        {
            // Double quotes
            if (line.Contains('"') && line.IndexOf('"') != line.LastIndexOf('"'))
            {
                dialogueLines++;
                continue;
            }

            // Single quotes (British style, require substantial content to avoid contractions)
            var firstSingle = line.IndexOf('\u2018'); // '
            var lastSingle = line.LastIndexOf('\u2019'); // '
            if (firstSingle >= 0 && lastSingle > firstSingle + 10)
            {
                dialogueLines++;
                continue;
            }

            // ASCII single quotes with substantial content
            if (line.Contains('\''))
            {
                var first = line.IndexOf('\'');
                var last = line.LastIndexOf('\'');
                if (last > first + 10)
                    dialogueLines++;
            }
        }

        return (double)dialogueLines / lines.Length;
    }
}

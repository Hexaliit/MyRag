using System.Text.RegularExpressions;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Extracts atomic propositions from evidence segments.
/// Based on Dense-X Retrieval: propositions are atomic, self-contained facts
/// that improve retrieval precision and reduce repetition.
///
/// Each proposition is:
/// - Unique: a distinct piece of meaning
/// - Atomic: cannot be further split
/// - Self-contained: includes necessary context
///
/// Reference: "Dense X Retrieval: What Retrieval Granularity Should We Use?"
/// (Chen et al., University of Washington / Tencent AI Lab)
/// </summary>
public static partial class PropositionExtractor
{
    // Patterns that indicate sentence boundaries for proposition extraction
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex SentenceBoundaryRx();

    // Patterns for extracting factual claims
    [GeneratedRegex(@"^(.*?(?:is|are|was|were|has|have|had|can|could|will|would|should|must|may|might)\s+.+?)[.!?]", RegexOptions.IgnoreCase)]
    private static partial Regex FactualClaimRx();

    // Quote extraction pattern
    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex QuoteRx();

    // Named entity pattern (capitalized sequences)
    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)*)\b")]
    private static partial Regex NamedEntityRx();

    /// <summary>
    /// Extract propositions from a segment's text.
    /// Returns atomic facts that can be independently verified and matched.
    /// </summary>
    public static List<Proposition> ExtractPropositions(
        EvidenceSegment segment,
        string? articleTitle = null)
    {
        var text = segment.Segment.Text;
        var propositions = new List<Proposition>();

        // 1. Split into sentences
        var sentences = SentenceBoundaryRx().Split(text)
            .Where(s => s.Trim().Length > 20)
            .ToList();

        // 2. Extract propositions from each sentence
        foreach (var sentence in sentences)
        {
            var props = ExtractFromSentence(sentence, segment, articleTitle);
            propositions.AddRange(props);
        }

        // 3. Deduplicate similar propositions within this segment
        propositions = DeduplicatePropositions(propositions);

        return propositions;
    }

    /// <summary>
    /// Extract propositions from all segments in a corpus.
    /// </summary>
    public static List<Proposition> ExtractFromCorpus(EvidenceCorpus corpus)
    {
        var allPropositions = new List<Proposition>();

        foreach (var segment in corpus.Segments)
        {
            var props = ExtractPropositions(segment, segment.ArticleTitle);
            allPropositions.AddRange(props);
        }

        return allPropositions;
    }

    /// <summary>
    /// Filter propositions to those most relevant to a section theme.
    /// </summary>
    public static List<Proposition> FilterForSection(
        List<Proposition> propositions,
        PlannedSection section,
        int maxPropositions = 15)
    {
        if (section.ThemeEmbedding == null)
            return propositions.Take(maxPropositions).ToList();

        // Score by theme similarity
        var scored = propositions
            .Where(p => p.Embedding != null)
            .Select(p => new
            {
                Proposition = p,
                ThemeSim = EmbeddingService.CosineSimilarity(section.ThemeEmbedding, p.Embedding!)
            })
            .OrderByDescending(x => x.ThemeSim)
            .Take(maxPropositions)
            .Select(x => x.Proposition)
            .ToList();

        return scored;
    }

    /// <summary>
    /// Remove propositions that were already used in previous sections.
    /// Uses embedding similarity for semantic deduplication.
    /// </summary>
    public static List<Proposition> RemoveUsedPropositions(
        List<Proposition> candidates,
        List<Proposition> usedPropositions,
        float similarityThreshold = 0.85f)
    {
        return candidates
            .Where(c => !IsUsed(c, usedPropositions, similarityThreshold))
            .ToList();
    }

    /// <summary>
    /// Format propositions as bullet points for LLM prompt.
    /// Groups by source article for better context.
    /// </summary>
    public static string FormatAsBullets(
        List<Proposition> propositions,
        bool includeSource = true)
    {
        var grouped = propositions
            .GroupBy(p => p.SourceArticle ?? "Unknown")
            .OrderByDescending(g => g.Count());

        var lines = new List<string>();

        foreach (var group in grouped)
        {
            if (includeSource)
                lines.Add($"From \"{group.Key}\":");

            foreach (var prop in group)
            {
                var bullet = prop.Type switch
                {
                    PropositionType.Quote => $"  • \"{prop.Text}\"",
                    PropositionType.Statistic => $"  • [STAT] {prop.Text}",
                    PropositionType.NamedEntity => $"  • [ENTITY] {prop.Text}",
                    _ => $"  • {prop.Text}"
                };
                lines.Add(bullet);
            }

            if (includeSource)
                lines.Add(""); // Blank line between sources
        }

        return string.Join("\n", lines);
    }

    private static List<Proposition> ExtractFromSentence(
        string sentence,
        EvidenceSegment segment,
        string? articleTitle)
    {
        var props = new List<Proposition>();
        var trimmed = sentence.Trim();

        // Skip if too short or too long
        if (trimmed.Length < 30 || trimmed.Length > 500)
            return props;

        // Determine proposition type
        var type = ClassifyProposition(trimmed);

        // Create the proposition
        var prop = new Proposition
        {
            Text = trimmed,
            Type = type,
            SourceSegment = segment,
            SourceArticle = articleTitle,
            Salience = (float)segment.Segment.SalienceScore
        };

        props.Add(prop);

        // Also extract any quotes as separate propositions
        var quotes = QuoteRx().Matches(trimmed);
        foreach (Match quote in quotes)
        {
            if (quote.Groups[1].Value.Length > 20)
            {
                props.Add(new Proposition
                {
                    Text = quote.Groups[1].Value,
                    Type = PropositionType.Quote,
                    SourceSegment = segment,
                    SourceArticle = articleTitle,
                    Salience = (float)(segment.Segment.SalienceScore * 1.2) // Boost quotes
                });
            }
        }

        return props;
    }

    private static PropositionType ClassifyProposition(string text)
    {
        var lower = text.ToLowerInvariant();

        // Statistics/numbers
        if (Regex.IsMatch(text, @"\d+%|\d+\.\d+|million|billion|thousand"))
            return PropositionType.Statistic;

        // Quotes
        if (text.StartsWith("\"") || text.Contains("said") || text.Contains("according to"))
            return PropositionType.Quote;

        // Named entity focus (starts with proper noun)
        if (char.IsUpper(text[0]) && NamedEntityRx().IsMatch(text[..Math.Min(50, text.Length)]))
            return PropositionType.NamedEntity;

        // Definitions/explanations
        if (lower.Contains(" is ") || lower.Contains(" are ") || lower.Contains(" means "))
            return PropositionType.Definition;

        // Process/how-to
        if (lower.Contains("step") || lower.Contains("first") || lower.Contains("then"))
            return PropositionType.Process;

        return PropositionType.Claim;
    }

    private static List<Proposition> DeduplicatePropositions(List<Proposition> propositions)
    {
        if (propositions.Count <= 1) return propositions;

        var unique = new List<Proposition>();

        foreach (var prop in propositions)
        {
            var isDupe = unique.Any(existing =>
                TextSimilarity(prop.Text, existing.Text) > 0.8f);

            if (!isDupe)
                unique.Add(prop);
        }

        return unique;
    }

    private static bool IsUsed(
        Proposition candidate,
        List<Proposition> used,
        float threshold)
    {
        if (candidate.Embedding == null)
            return false;

        return used.Any(u =>
            u.Embedding != null &&
            EmbeddingService.CosineSimilarity(candidate.Embedding, u.Embedding) > threshold);
    }

    private static float TextSimilarity(string a, string b)
    {
        // Simple Jaccard similarity on words
        var wordsA = a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (float)intersection / union : 0f;
    }
}

/// <summary>
/// An atomic proposition extracted from evidence.
/// </summary>
public record Proposition
{
    public required string Text { get; init; }
    public PropositionType Type { get; init; } = PropositionType.Claim;
    public EvidenceSegment? SourceSegment { get; init; }
    public string? SourceArticle { get; init; }
    public float Salience { get; init; }
    public float[]? Embedding { get; set; }
}

public enum PropositionType
{
    Claim,      // General factual claim
    Quote,      // Direct quote from source
    Statistic,  // Number/percentage/metric
    Definition, // "X is Y" style definition
    Process,    // Step-by-step or how-to
    NamedEntity // Focuses on a specific entity
}

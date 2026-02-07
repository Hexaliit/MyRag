using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Technical;

/// <summary>
///     Fast keyword-based classifier for technical/academic content.
///     Uses weighted keyword matching plus structural signals (bibliography, citations, DOIs).
/// </summary>
public static partial class TechnicalKeywordClassifier
{
    // High-confidence academic terms (weighted 3x)
    private static readonly HashSet<string> StrongTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "methodology", "conclusion", "references", "bibliography", "et al.",
        "hypothesis", "theorem", "corollary", "lemma", "proof",
        "p-value", "confidence interval", "standard deviation", "regression",
        "arXiv", "IEEE", "ACM", "Springer", "NeurIPS", "ICML", "CVPR",
        "algorithm", "pseudocode", "benchmark", "ablation", "baseline",
        "peer-reviewed", "journal", "proceedings", "preprint",
        "correlation", "statistically significant", "null hypothesis",
        "related work", "prior work", "state-of-the-art", "SOTA",
        "evaluation metric", "F1 score", "precision", "recall",
        "hyperparameter", "cross-validation", "overfitting", "underfitting",
        "gradient descent", "backpropagation", "loss function",
        "reproducibility", "supplementary material", "appendix"
    };

    // Medium-confidence terms (weighted 1x)
    private static readonly HashSet<string> ModerateTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "study", "research", "analysis", "experiment", "findings", "results",
        "model", "framework", "approach", "method", "technique",
        "figure", "table", "equation", "section",
        "propose", "demonstrate", "validate", "implement",
        "dataset", "corpus", "annotation", "evaluation",
        "accuracy", "performance", "comparison", "metric",
        "architecture", "pipeline", "feature", "representation",
        "training", "inference", "prediction", "classification",
        "optimization", "parameter", "convergence", "iteration",
        "survey", "review", "taxonomy", "ontology"
    };

    // Regex to detect bibliography section headers
    [GeneratedRegex(@"^(?:References|Bibliography|Works\s+Cited)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BibliographySectionRegex();

    // Regex to detect inline citations like [1], [2,3], (Smith, 2020)
    [GeneratedRegex(@"\[(\d+(?:\s*[-,]\s*\d+)*)\]|\([A-Z][a-z]+(?:\s+et\s+al\.?)?,?\s*\d{4}\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex InlineCitationRegex();

    // DOI pattern
    [GeneratedRegex(@"10\.\d{4,}/[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex DoiRegex();

    // arXiv pattern
    [GeneratedRegex(@"arXiv:\s*\d{4}\.\d{4,5}", RegexOptions.IgnoreCase)]
    private static partial Regex ArxivRegex();

    /// <summary>
    ///     Classify text as technical/academic content based on keyword density and structural signals.
    /// </summary>
    public static DomainClassification Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DomainClassification("technical", "Technical/Academic Domain", 0.0);

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

        // Structural bonuses
        if (BibliographySectionRegex().IsMatch(text))
            confidence += 0.2;

        // Citation density bonus
        var citationCount = InlineCitationRegex().Matches(text).Count;
        var citationsPerKWords = citationCount / (totalWords / 1000.0);
        if (citationsPerKWords > 2)
            confidence += 0.15;

        // DOI or arXiv presence bonus
        if (DoiRegex().IsMatch(text) || ArxivRegex().IsMatch(text))
            confidence += 0.1;

        // Floor: at least 2 strong terms needed for meaningful confidence
        if (strongHits < 2)
            confidence = Math.Min(confidence, 0.4);

        confidence = Math.Min(1.0, confidence);

        return new DomainClassification(
            "technical",
            "Technical/Academic Domain",
            Math.Round(confidence, 4),
            new Dictionary<string, object?>
            {
                ["strong_hits"] = strongHits,
                ["moderate_hits"] = moderateHits,
                ["citation_count"] = citationCount,
                ["has_bibliography"] = BibliographySectionRegex().IsMatch(text),
                ["has_doi"] = DoiRegex().IsMatch(text),
                ["has_arxiv"] = ArxivRegex().IsMatch(text)
            });
    }
}

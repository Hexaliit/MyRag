using System.Text.RegularExpressions;

namespace DoomSummarizer.Services;

/// <summary>
/// Detects query intent to drive synthesis strategy selection.
/// </summary>
public static partial class QueryTypeDetector
{
    public static QueryType Detect(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return QueryType.General;

        var q = query.ToLowerInvariant();

        if (TimelinePattern().IsMatch(q))
            return QueryType.Timeline;

        if (ComparisonPattern().IsMatch(q))
            return QueryType.Comparison;

        if (ExplainerPattern().IsMatch(q))
            return QueryType.Explainer;

        if (RoundupPattern().IsMatch(q))
            return QueryType.Roundup;

        return QueryType.General;
    }

    /// <summary>
    /// Source quality multipliers for a given query type.
    /// Applied after existing source weights in the RRF pipeline.
    /// </summary>
    public static double GetSourceQualityMultiplier(QueryType queryType, string? url)
    {
        if (string.IsNullOrEmpty(url))
            return 1.0;

        var domain = ExtractDomain(url);

        return queryType switch
        {
            QueryType.Timeline or QueryType.Explainer => GetAcademicWeight(domain),
            QueryType.Roundup => GetNewsWeight(domain),
            _ => 1.0
        };
    }

    private static double GetAcademicWeight(string domain)
    {
        // Primary sources get a boost for history/explainer queries
        if (PrimarySourceDomains.Any(d => domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return 1.3;

        // Low-signal blog platforms get penalized
        if (LowSignalDomains.Any(d => domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return 0.7;

        return 1.0;
    }

    private static double GetNewsWeight(string domain)
    {
        // News sources get a boost for roundup queries
        if (NewsDomains.Any(d => domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return 1.2;

        // Academic sources slightly deprioritized for news roundups
        if (PrimarySourceDomains.Any(d => domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return 0.9;

        return 1.0;
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.ToLowerInvariant();
        }
        catch
        {
            return url.ToLowerInvariant();
        }
    }

    private static readonly string[] PrimarySourceDomains =
    [
        "arxiv.org", "openreview.net", "aclanthology.org", "neurips.cc",
        "proceedings.mlr.press", "openai.com", "ai.google", "research.google",
        "research.facebook.com", "research.meta.com", "deepmind.google",
        "microsoft.com/research", "nature.com", "science.org", "ieee.org",
        "acm.org", "wikipedia.org"
    ];

    private static readonly string[] LowSignalDomains =
    [
        "medium.com", "dev.to", "linkedin.com", "towardsdatascience.com",
        "hackernoon.com", "analytics-vidhya"
    ];

    private static readonly string[] NewsDomains =
    [
        "bbc.co.uk", "bbc.com", "theguardian.com", "reuters.com",
        "arstechnica.com", "theverge.com", "techcrunch.com", "wired.com",
        "theregister.com", "engadget.com", "zdnet.com"
    ];

    [GeneratedRegex(@"\b(history|evolution|timeline|origin|how\s+did\s+\w+\s+(develop|start|begin|evolve|emerge)|chronolog|over\s+the\s+years|through\s+the\s+ages)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimelinePattern();

    [GeneratedRegex(@"\b(vs\.?|versus|compar|difference\s+between|which\s+is\s+better|pros?\s+and\s+cons?|trade.?offs?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonPattern();

    [GeneratedRegex(@"\b(how\s+does|what\s+is|explain|why\s+does|how\s+do|what\s+are|overview\s+of|introduction\s+to|guide\s+to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExplainerPattern();

    [GeneratedRegex(@"\b(this\s+week|latest|recent|news|interesting|roundup|digest|weekly|what.?s\s+new|trending|top\s+\d+|best\s+of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoundupPattern();
}

public enum QueryType
{
    General,
    Timeline,
    Comparison,
    Explainer,
    Roundup
}

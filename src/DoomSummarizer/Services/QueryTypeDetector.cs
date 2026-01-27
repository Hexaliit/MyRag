using System.Text.RegularExpressions;
using DoomSummarizer.Models;

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
    /// Override query type using sentinel intent when available.
    /// The sentinel LLM is better at distinguishing QA from roundup
    /// (e.g., "What's the SNL host this week?" is QA, not roundup).
    /// </summary>
    public static QueryType Detect(string? query, SentinelIntent? sentinelIntent)
    {
        var heuristic = Detect(query);

        if (sentinelIntent == null)
            return heuristic;

        // Sentinel QA/howto always overrides Roundup — the user asked a specific question
        if (sentinelIntent.Intent is "qa" or "howto" && heuristic == QueryType.Roundup)
            return QueryType.Explainer;

        // Sentinel research/deep_dive overrides Roundup
        if (sentinelIntent.Intent is "research" or "deep_dive" && heuristic == QueryType.Roundup)
            return QueryType.Explainer;

        return heuristic;
    }

    /// <summary>
    /// Whether the roundup query implies "today" (last 24-48h) date-gating.
    /// </summary>
    public static bool ImpliesDateGating(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        return DateGatingPattern().IsMatch(query.ToLowerInvariant());
    }

    /// <summary>
    /// Check if a content item looks like "on this day" / historical drift
    /// that should be penalized in roundup queries.
    /// </summary>
    public static bool IsTopicDrift(ContentItem item)
    {
        var text = $"{item.Title} {item.Content ?? ""}";
        return TopicDriftPattern().IsMatch(text);
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

    /// <summary>
    /// For roundup queries, compute a freshness multiplier based on publication date.
    /// Items older than the cutoff get demoted.
    /// </summary>
    public static double GetFreshnessMultiplier(ContentItem item, TimeSpan maxAge)
    {
        var age = DateTimeOffset.UtcNow - item.CreatedAt;
        if (age <= maxAge) return 1.0;

        // Gradual decay: items up to 2x maxAge get 0.5, beyond that 0.2
        var ratio = age / maxAge;
        if (ratio <= 2.0) return 0.5;
        return 0.2;
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

    [GeneratedRegex(@"\b(how\s+does|what\s+is|what'?s\s+the|what'?s\s+a\b|explain|why\s+does|how\s+do|what\s+are|who\s+is|who'?s\s+the|overview\s+of|introduction\s+to|guide\s+to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExplainerPattern();

    [GeneratedRegex(@"\b(today|this\s+week|this\s+morning|latest|recent|news|interesting|stories|headlines|roundup|round.?up|digest|weekly|daily|what.?s\s+new|what\s+happened|trending|top\s+\d+|best\s+of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoundupPattern();

    /// <summary>
    /// Matches queries that imply "recent / today" date constraint.
    /// </summary>
    [GeneratedRegex(@"\b(today|this\s+morning|right\s+now|past\s+hour|last\s+\d+\s+hours?|this\s+afternoon|just\s+happened|breaking)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateGatingPattern();

    /// <summary>
    /// Matches content that looks like "on this day in history" drift — should be penalized in roundups.
    /// </summary>
    [GeneratedRegex(@"\b(on\s+this\s+day|today\s+in\s+history|born\s+on\s+this\s+day|anniversary\s+of|years?\s+ago\s+today|this\s+day\s+in|historical\s+event)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TopicDriftPattern();
}

public enum QueryType
{
    General,
    Timeline,
    Comparison,
    Explainer,
    Roundup
}

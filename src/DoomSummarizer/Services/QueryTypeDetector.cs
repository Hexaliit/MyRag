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
    /// Whether recency filtering should be applied.
    /// Uses sentinel LLM extraction only - no hardcoded regex patterns.
    /// </summary>
    public static bool ImpliesDateGating(SentinelIntent? sentinelIntent)
    {
        if (sentinelIntent == null) return false;
        return sentinelIntent.RequiresFresh
            || sentinelIntent.TimeSensitivity is "today" or "breaking" or "week"
            || sentinelIntent.DateRange != null;
    }

    /// <summary>
    /// Get the maximum age for freshness filtering based on sentinel LLM extraction.
    /// NO regex fallbacks - trust the LLM to parse natural language.
    /// </summary>
    public static TimeSpan GetMaxAge(SentinelIntent? sentinelIntent, string? query = null)
    {
        // Sentinel date range is authoritative - LLM parsed the natural language
        if (sentinelIntent?.DateRange != null)
        {
            var unit = sentinelIntent.DateRange.Unit?.ToLowerInvariant();
            var count = sentinelIntent.DateRange.Count ?? 1;
            return unit switch
            {
                "hour" => TimeSpan.FromHours(count),
                "day" => TimeSpan.FromDays(count),
                "week" => TimeSpan.FromDays(count * 7),
                "month" => TimeSpan.FromDays(count * 30),
                "year" => TimeSpan.FromDays(count * 365),
                _ => TimeSpan.FromDays(14) // "recent" without unit → 2 weeks
            };
        }

        // TimeSensitivity: LLM's interpretation of urgency
        var timeSens = sentinelIntent?.TimeSensitivity?.ToLowerInvariant();
        return timeSens switch
        {
            "breaking" => TimeSpan.FromHours(24),
            "today" => TimeSpan.FromHours(48),
            "week" => TimeSpan.FromDays(7),
            _ when sentinelIntent?.RequiresFresh == true => TimeSpan.FromDays(14), // "recent/latest" → 2 weeks
            _ => TimeSpan.FromDays(30) // No temporal constraint → generous window
        };
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
    [GeneratedRegex(@"\b(today|this\s+morning|right\s+now|past\s+hour|last\s+\d+\s+(hours?|days?|weeks?)|this\s+afternoon|just\s+happened|breaking|recent|latest|new|current|this\s+week|past\s+few\s+days)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateGatingPattern();

    /// <summary>
    /// Matches content that looks like "on this day in history" drift — should be penalized in roundups.
    /// </summary>
    [GeneratedRegex(@"\b(on\s+this\s+day|today\s+in\s+history|born\s+on\s+this\s+day|anniversary\s+of|years?\s+ago\s+today|this\s+day\s+in|historical\s+event)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TopicDriftPattern();

    // --- GraphRAG Scope Detection ---

    /// <summary>
    /// Detect the GraphRAG scope of a query: Local (specific), Global (sensemaking), or Connective (DRIFT).
    /// Uses heuristic patterns first, then sentinel intent when available.
    /// </summary>
    public static GraphScope DetectGraphScope(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GraphScope.Local;

        var q = query.ToLowerInvariant();

        if (GlobalScopePattern().IsMatch(q))
            return GraphScope.Global;

        if (ConnectiveScopePattern().IsMatch(q))
            return GraphScope.Connective;

        return GraphScope.Local;
    }

    /// <summary>
    /// Override graph scope using sentinel intent when available.
    /// Sentinel "deep_dive" → Connective, "roundup" → Global.
    /// </summary>
    public static GraphScope DetectGraphScope(string? query, SentinelIntent? sentinelIntent)
    {
        var heuristic = DetectGraphScope(query);

        if (sentinelIntent == null)
            return heuristic;

        // Sentinel graph_scope overrides heuristic when present
        if (!string.IsNullOrWhiteSpace(sentinelIntent.GraphScope))
        {
            return sentinelIntent.GraphScope.ToLowerInvariant() switch
            {
                "global" => GraphScope.Global,
                "connective" => GraphScope.Connective,
                _ => heuristic
            };
        }

        // Infer from intent type when graph_scope not explicitly set
        if (sentinelIntent.Intent is "deep_dive" && heuristic == GraphScope.Local)
            return GraphScope.Connective;

        return heuristic;
    }

    /// <summary>
    /// Sensemaking queries: "what are the main themes", "summarize the key topics",
    /// "overview of all", "what patterns", "common threads", "big picture".
    /// </summary>
    [GeneratedRegex(@"\b(main\s+themes?|key\s+topics?|summarize\s+(all|everything|the\s+key)|overview\s+of\s+(all|the|my)|what\s+patterns?|common\s+threads?|big\s+picture|recurring\s+topics?|what\s+topics?\s+(do|does|are)|corpus|across\s+(all|the)\s+(documents?|articles?|sources?)|general\s+themes?|broad\s+trends?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalScopePattern();

    /// <summary>
    /// Connective queries: "how does X relate to Y", "connection between",
    /// "what links", "relationship between", "compare across".
    /// </summary>
    [GeneratedRegex(@"\b(how\s+does?\s+\w+\s+relate|relat(e|es|ed|ion|ionship)\s+(to|between|with)|connect(ion|ed|s)?\s+(between|to|with|across)|what\s+links?|bridge\s+between|compare\s+across|in\s+common\s+between|shared\s+between|overlap\s+between|how\s+are\s+\w+\s+(and|&)\s+\w+\s+(connected|related|linked))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectiveScopePattern();
}

public enum QueryType
{
    General,
    Timeline,
    Comparison,
    Explainer,
    Roundup
}

/// <summary>
/// GraphRAG-style query scope (Microsoft Research, 2024).
/// Determines whether a query needs chunk-level retrieval (Local),
/// corpus-level community summaries (Global), or entity graph traversal (Connective).
/// See: https://microsoft.github.io/graphrag/
/// </summary>
public enum GraphScope
{
    /// <summary>Specific question — vector search + graph neighborhood. Default.</summary>
    Local,

    /// <summary>Sensemaking — "what are the main themes?", "summarize the key topics".</summary>
    Global,

    /// <summary>Connective — "how does X relate to Y?", "connections between A and B".</summary>
    Connective
}

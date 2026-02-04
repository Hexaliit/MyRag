using DoomSummarizer.Models;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Unified parameter bag passed to <see cref="ISourcePlugin.FetchAsync" />.
///     Parsed from the raw source string (e.g. "reddit:csharp", "arxiv:cat:cs.AI").
/// </summary>
public record SourceFetchContext
{
    /// <summary>
    ///     Keys that are composite (include underscore) and should be matched as-is.
    /// </summary>
    private static readonly HashSet<string> CompositeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnews_topic", "brave_search", "brave_news", "news_api", "news_data", "serper_news",
        "reddit_comments"
    };

    /// <summary>Original source string as typed by the user (e.g. "reddit:csharp").</summary>
    public required string RawSource { get; init; }

    /// <summary>Lowercase key used for plugin lookup (e.g. "reddit").</summary>
    public required string SourceKey { get; init; }

    /// <summary>Colon-separated sub-parameters after the key (e.g. ["csharp"] or ["cat", "cs.AI"]).</summary>
    public IReadOnlyList<string> SubParams { get; init; } = [];

    /// <summary>Search query (from interpreted prompt or explicit).</summary>
    public string? Query { get; init; }

    /// <summary>Raw user prompt text.</summary>
    public string? RawPrompt { get; init; }

    /// <summary>Max items to fetch from this source.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>Current vibe/tone (e.g. "doom", "hopeful", "neutral").</summary>
    public string Vibe { get; init; } = "neutral";

    /// <summary>Progress callback for status updates.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>Full configuration.</summary>
    public DoomConfig? Config { get; init; }

    /// <summary>
    ///     Parse a raw source string into a <see cref="SourceFetchContext" />.
    ///     Handles formats: "hn", "reddit:csharp", "arxiv:cat:cs.AI", "gnews_topic:HEALTH".
    /// </summary>
    public static SourceFetchContext Parse(
        string rawSource,
        string? query = null,
        int limit = 20,
        string vibe = "neutral",
        DoomConfig? config = null,
        string? rawPrompt = null,
        Action<string>? progress = null)
    {
        var src = rawSource.ToLowerInvariant();
        var parts = src.Split(':');
        var key = parts[0];
        var subParams = parts.Length > 1 ? parts[1..].ToList() : [];

        // Handle composite keys like "gnews_topic", "brave_search", "news_api" etc.
        // These use underscore, not colon, to separate the key from parameters.
        // The full "gnews_topic" is the key; the part after colon is the parameter.
        if (key.Contains('_') || CompositeKeys.Contains(key))
        {
            // Check if "key:param" should be parsed as composite
            // e.g. "gnews_topic:HEALTH" → key="gnews_topic", subParams=["HEALTH"]
            // But "brave:query" → key="brave", subParams=["query"]
        }

        return new SourceFetchContext
        {
            RawSource = rawSource,
            SourceKey = key,
            SubParams = subParams,
            Query = query,
            RawPrompt = rawPrompt,
            Limit = limit,
            Vibe = vibe,
            Progress = progress,
            Config = config
        };
    }

    /// <summary>
    ///     Parse with composite key awareness. Tries longest match first.
    ///     E.g. "gnews_topic:HEALTH" → key="gnews_topic", sub=["HEALTH"]
    ///     "brave:AI agents"   → key="brave", sub=["AI agents"]
    /// </summary>
    public static SourceFetchContext ParseWithCompositeKeys(
        string rawSource,
        IEnumerable<string> registeredKeys,
        string? query = null,
        int limit = 20,
        string vibe = "neutral",
        DoomConfig? config = null,
        string? rawPrompt = null,
        Action<string>? progress = null)
    {
        var src = rawSource.ToLowerInvariant();

        // Try longest registered key match first
        var bestMatch = registeredKeys
            .Where(k => src == k || src.StartsWith(k + ":"))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();

        string key;
        IReadOnlyList<string> subParams;

        if (bestMatch != null)
        {
            key = bestMatch;
            subParams = src.Length > bestMatch.Length + 1
                ? src[(bestMatch.Length + 1)..].Split(':').ToList()
                : [];
        }
        else
        {
            var parts = src.Split(':');
            key = parts[0];
            subParams = parts.Length > 1 ? parts[1..].ToList() : [];
        }

        return new SourceFetchContext
        {
            RawSource = rawSource,
            SourceKey = key,
            SubParams = subParams,
            Query = query,
            RawPrompt = rawPrompt,
            Limit = limit,
            Vibe = vibe,
            Progress = progress,
            Config = config
        };
    }
}
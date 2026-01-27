using System.Text.Json.Serialization;

namespace DoomSummarizer.Models;

public record DoomConfig
{
    public SourcesConfig Sources { get; init; } = new();
    public SourceFilterConfig SourceFilter { get; init; } = new();
    public OllamaConfig Ollama { get; init; } = new();
    public EmbeddingConfig Embedding { get; init; } = new();
    public OutputConfig Output { get; init; } = new();
    public StorageConfig Storage { get; init; } = new();
    public LinkFollowingConfig LinkFollowing { get; init; } = new();
    public Dictionary<string, string> Vibes { get; init; } = new();
    public List<ApiKeyEntry> Keys { get; init; } = [];
    public ApiBudgetConfig ApiBudget { get; init; } = new();
}

public record SourcesConfig
{
    public HackerNewsConfig HackerNews { get; init; } = new();
    public RedditConfig Reddit { get; init; } = new();
    public List<WebsiteConfig> Websites { get; init; } = [];
}

public record HackerNewsConfig
{
    public bool Enabled { get; init; } = true;
    public List<string> Sections { get; init; } = ["top", "best"];
    public int MaxStories { get; init; } = 30;
    public int MinScore { get; init; } = 50;
}

public record RedditConfig
{
    public bool Enabled { get; init; } = true;
    public List<string> Subreddits { get; init; } = ["programming", "csharp", "dotnet"];
    public string Sort { get; init; } = "hot";
    public int MaxPosts { get; init; } = 25;
    public int MinScore { get; init; } = 100;
}

public record WebsiteConfig
{
    public string Url { get; init; } = "";
    public string? Selector { get; init; }
    public bool UsePlaywright { get; init; }
}

/// <summary>
/// Global source filtering and reliability weighting.
/// Controls which domains are allowed/blocked and how sources are weighted in RRF scoring.
/// </summary>
public record SourceFilterConfig
{
    /// <summary>
    /// If non-empty, ONLY items from these domains are kept (allowlist mode).
    /// Useful for intranet/focused crawling. Matches domain suffix (e.g. "bbc.co.uk").
    /// </summary>
    public List<string> AllowedDomains { get; init; } = [];

    /// <summary>
    /// Items from these domains are removed post-fetch.
    /// Matches domain suffix (e.g. "medium.com" blocks all Medium articles).
    /// </summary>
    public List<string> BlockedDomains { get; init; } = [];

    /// <summary>
    /// Source reliability weights applied as RRF score multipliers.
    /// Key = source name (hn, reddit, bbc, gnews, search) or domain substring (reuters.com, bbc.co.uk).
    /// Value = multiplier: 1.0 = neutral, >1 = boost, less than 1 = penalize, 0 = effectively block.
    /// Unmatched sources default to 1.0.
    /// </summary>
    public Dictionary<string, double> Weights { get; init; } = new();
}

public record OllamaConfig
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "gemma3:4b";
    public string SentinelModel { get; init; } = "qwen3:0.6b";
    public string EmbedModel { get; init; } = "nomic-embed-text";
    public double Temperature { get; init; } = 0.4;
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Context window size (tokens) for the main model. Used to budget evidence content.</summary>
    public int ContextSize { get; init; } = 8192;

    /// <summary>Context window size (tokens) for the sentinel model.</summary>
    public int SentinelContextSize { get; init; } = 32768;

    /// <summary>
    /// Compute max chars of evidence content per item for the given model context.
    /// Reserves space for prompt overhead (~300 tokens) and output (~500 tokens).
    /// Assumes ~3.5 chars per token for English text.
    /// </summary>
    public int MaxEvidenceCharsPerItem(bool sentinel, int itemCount)
    {
        var ctx = sentinel ? SentinelContextSize : ContextSize;
        var availableTokens = ctx - 800; // reserve for prompt + output
        var perItem = Math.Max(100, availableTokens / Math.Max(1, itemCount));
        return (int)(perItem * 3.5);
    }
}

public record EmbeddingConfig
{
    public string Backend { get; init; } = "onnx";
    public string Model { get; init; } = "all-MiniLM-L6-v2";
    public double SimilarityThreshold { get; init; } = 0.95;
}

public record OutputConfig
{
    public string Format { get; init; } = "markdown";
    public int MaxSummaryLength { get; init; } = 500;
    public bool IncludeLinks { get; init; } = true;
    public bool GroupByTopic { get; init; } = true;
}

public record StorageConfig
{
    public string DbPath { get; init; } = "~/.doomsummarizer/doom.db";
    public int RetentionDays { get; init; } = 30;
}

public record LinkFollowingConfig
{
    /// <summary>Enable one-hop link following to enrich article content.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum links to follow per article.</summary>
    public int MaxLinksPerArticle { get; init; } = 3;

    /// <summary>Maximum total linked pages to fetch across all articles.</summary>
    public int MaxTotalLinks { get; init; } = 15;

    /// <summary>Maximum content length (chars) to extract per linked page.</summary>
    public int MaxContentLength { get; init; } = 2000;

    /// <summary>Timeout in seconds for each linked page fetch.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Domains to never follow links to (social media, login, etc.).</summary>
    public List<string> BlockedDomains { get; init; } =
    [
        "facebook.com", "twitter.com", "x.com", "instagram.com",
        "linkedin.com", "youtube.com", "tiktok.com",
        "accounts.google.com", "login.", "auth.",
        "play.google.com", "apps.apple.com"
    ];

    /// <summary>File extensions to skip.</summary>
    public List<string> BlockedExtensions { get; init; } =
    [
        ".pdf", ".zip", ".tar", ".gz", ".exe", ".dmg",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp",
        ".mp3", ".mp4", ".mov", ".avi", ".mkv"
    ];
}

/// <summary>
/// A single API service definition.
/// Each entry is self-contained: name, key, service-specific config, enabled flag, and budget.
/// API keys loaded from config JSON, user secrets ("keys:0:GoogleSearch"), or env vars (DOOM_GOOGLE_SEARCH).
/// </summary>
public record ApiKeyEntry
{
    /// <summary>Service identifier: "google_search", "google_places".</summary>
    public string Name { get; init; } = "";

    /// <summary>API key. Prefer user-secrets or env vars over storing in plain text.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Service-specific extra ID (e.g., Google Custom Search Engine ID).</summary>
    public string? SearchEngineId { get; init; }

    /// <summary>Whether this service is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Max requests per day for this service. 0 = use global limit.</summary>
    public int MaxRequestsPerDay { get; init; } = 100;

    /// <summary>Lifetime request cap. 0 = unlimited.</summary>
    public int MaxRequests { get; init; } = 0;

    /// <summary>Daily budget in USD for this service. 0 = use global budget.</summary>
    public double DailyBudgetUsd { get; init; } = 0;

    /// <summary>Estimated cost per API call in USD.</summary>
    public double CostPerRequest { get; init; } = 0.005;

    /// <summary>Context window size (tokens) for the main model. Used for evidence budgeting.</summary>
    public int ContextSize { get; init; } = 0;

    /// <summary>Context window size (tokens) for the sentinel model.</summary>
    public int SentinelContextSize { get; init; } = 0;

    /// <summary>Minimum delay (ms) between API requests. Prevents rate limiting on free tiers.</summary>
    public int RateLimitMs { get; init; } = 200;

    /// <summary>Max retry attempts on 429/5xx errors (0 = no retry).</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Consecutive failures before circuit breaker opens. 0 = disabled.</summary>
    public int CircuitBreakerThreshold { get; init; } = 3;

    /// <summary>Seconds before circuit breaker resets to half-open.</summary>
    public int CircuitBreakerResetSeconds { get; init; } = 60;
}

/// <summary>
/// Global budget controls across all paid/limited APIs.
/// Individual service limits on each ApiKeyEntry override these when set.
/// </summary>
public record ApiBudgetConfig
{
    /// <summary>Global daily request limit across all paid APIs. 0 = unlimited.</summary>
    public int GlobalMaxRequestsPerDay { get; init; } = 200;

    /// <summary>Global daily budget in USD. 0 = unlimited.</summary>
    public double GlobalDailyBudgetUsd { get; init; } = 2.0;
}

// JSON serialization context for AOT
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DoomConfig))]
[JsonSerializable(typeof(SourcesConfig))]
[JsonSerializable(typeof(SourceFilterConfig))]
[JsonSerializable(typeof(HackerNewsConfig))]
[JsonSerializable(typeof(RedditConfig))]
[JsonSerializable(typeof(WebsiteConfig))]
[JsonSerializable(typeof(OllamaConfig))]
[JsonSerializable(typeof(EmbeddingConfig))]
[JsonSerializable(typeof(OutputConfig))]
[JsonSerializable(typeof(StorageConfig))]
[JsonSerializable(typeof(LinkFollowingConfig))]
[JsonSerializable(typeof(ApiKeyEntry))]
[JsonSerializable(typeof(ApiBudgetConfig))]
[JsonSerializable(typeof(List<ApiKeyEntry>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public partial class DoomConfigContext : JsonSerializerContext;

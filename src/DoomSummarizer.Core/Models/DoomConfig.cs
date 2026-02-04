using System.Text.Json.Serialization;

namespace DoomSummarizer.Models;

public record DoomConfig
{
    public string? Profile { get; init; }
    public SourcesConfig Sources { get; init; } = new();
    public SourceFilterConfig SourceFilter { get; init; } = new();
    public OllamaConfig Ollama { get; init; } = new();
    public EmbeddingConfig Embedding { get; init; } = new();
    public OutputConfig Output { get; init; } = new();
    public StorageConfig Storage { get; init; } = new();
    public LinkFollowingConfig LinkFollowing { get; init; } = new();
    public EmailConfig Email { get; init; } = new();
    public PluginsConfig Plugins { get; init; } = new();
    public LlamaSharpConfigSection LlamaSharp { get; init; } = new();
    public Dictionary<string, string> Vibes { get; init; } = new();
    public List<ApiKeyEntry> Keys { get; init; } = [];
    public ApiBudgetConfig ApiBudget { get; init; } = new();
}

/// <summary>
///     LLamaSharp local GGUF inference settings that can be overridden via config profiles.
///     Nullable fields indicate "use the LLamaSharpConfig defaults".
/// </summary>
public record LlamaSharpConfigSection
{
    public bool? Enabled { get; init; }
    public string? SynthesisModel { get; init; }
    public string? SentinelModel { get; init; }
    public uint? ContextSize { get; init; }
    public int? GpuLayerCount { get; init; }
    public int? BatchSize { get; init; }
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
///     Global source filtering and reliability weighting.
///     Controls which domains are allowed/blocked and how sources are weighted in RRF scoring.
/// </summary>
public record SourceFilterConfig
{
    /// <summary>
    ///     If non-empty, ONLY items from these domains are kept (allowlist mode).
    ///     Useful for intranet/focused crawling. Matches domain suffix (e.g. "bbc.co.uk").
    /// </summary>
    public List<string> AllowedDomains { get; init; } = [];

    /// <summary>
    ///     Items from these domains are removed post-fetch.
    ///     Matches domain suffix (e.g. "medium.com" blocks all Medium articles).
    /// </summary>
    public List<string> BlockedDomains { get; init; } = [];

    /// <summary>
    ///     Source reliability weights applied as RRF score multipliers.
    ///     Key = source name (hn, reddit, bbc, gnews, search) or domain substring (reuters.com, bbc.co.uk).
    ///     Value = multiplier: 1.0 = neutral, >1 = boost, less than 1 = penalize, 0 = effectively block.
    ///     Unmatched sources default to 1.0.
    /// </summary>
    public Dictionary<string, double> Weights { get; init; } = new();
}

// OllamaConfig is now defined in LucidRAG.LLM (Models/OllamaConfig.cs)

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

// ApiKeyEntry is now defined in LucidRAG.LLM (Models/ApiKeyEntry.cs)

/// <summary>
///     Email delivery configuration. Supports SMTP (via MailKit) and SendGrid.
///     API keys can be stored in user secrets: dotnet user-secrets set "SendGrid" "SG.xxx"
/// </summary>
public record EmailConfig
{
    /// <summary>Email delivery provider: "smtp" or "sendgrid".</summary>
    public string Provider { get; init; } = "smtp";

    /// <summary>Whether email delivery is enabled.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Sender email address (From).</summary>
    public string FromAddress { get; init; } = "";

    /// <summary>Sender display name.</summary>
    public string FromName { get; init; } = "DoomSummarizer";

    /// <summary>Default recipient(s). Comma-separated for multiple.</summary>
    public string ToAddresses { get; init; } = "";

    /// <summary>Email subject line template. Use {{DATE}} and {{QUERY}} placeholders.</summary>
    public string SubjectTemplate { get; init; } = "Doom Scroll Digest — {{DATE}}";

    /// <summary>Output template to use for email body (e.g., "email", "newsletter").</summary>
    public string Template { get; init; } = "email";

    /// <summary>SMTP settings (used when Provider = "smtp").</summary>
    public SmtpConfig Smtp { get; init; } = new();

    /// <summary>SendGrid API key (prefer user secrets or env var DOOM_SENDGRID).</summary>
    public string? SendGridApiKey { get; init; }
}

/// <summary>SMTP connection settings for MailKit.</summary>
public record SmtpConfig
{
    public string Host { get; init; } = "smtp.gmail.com";
    public int Port { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }
}

/// <summary>
///     Plugin management configuration. Controls which plugins are enabled,
///     auto-install behavior, and per-plugin overrides.
/// </summary>
public record PluginsConfig
{
    /// <summary>Enable runtime plugin loading from ~/.doomsummarizer/plugins/.</summary>
    public bool EnableRuntimePlugins { get; init; } = true;

    /// <summary>Auto-install these plugins on first run (shorthand or full NuGet ID).</summary>
    public List<string> AutoInstall { get; init; } = [];

    /// <summary>Disabled plugin keys — these won't be loaded even if installed.</summary>
    public List<string> Disabled { get; init; } = [];

    /// <summary>
    ///     Per-plugin settings. Key = plugin primary key (e.g., "hn", "reddit", "search").
    ///     Values are passed to the plugin's InitializeAsync as configuration overrides.
    /// </summary>
    public Dictionary<string, PluginSettings> Settings { get; init; } = new();
}

/// <summary>
///     Per-plugin configuration overrides. Stored in config under plugins.settings.[key].
/// </summary>
public record PluginSettings
{
    /// <summary>Whether this plugin is enabled. Overrides the global disabled list.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum items to fetch per invocation (overrides --limit for this source).</summary>
    public int? MaxItems { get; init; }

    /// <summary>Plugin-specific key-value options. Interpretation depends on the plugin.</summary>
    public Dictionary<string, string> Options { get; init; } = new();
}

// ApiBudgetConfig is now defined in Mostlylucid.DocSummarizer.Resilience.
// Re-exported via global using in Services/ApiBudgetService.cs.

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
[JsonSerializable(typeof(EmailConfig))]
[JsonSerializable(typeof(SmtpConfig))]
[JsonSerializable(typeof(PluginsConfig))]
[JsonSerializable(typeof(PluginSettings))]
[JsonSerializable(typeof(LlamaSharpConfigSection))]
[JsonSerializable(typeof(List<ApiKeyEntry>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public partial class DoomConfigContext : JsonSerializerContext;
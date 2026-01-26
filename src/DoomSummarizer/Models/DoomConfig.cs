using System.Text.Json.Serialization;

namespace DoomSummarizer.Models;

public record DoomConfig
{
    public SourcesConfig Sources { get; init; } = new();
    public OllamaConfig Ollama { get; init; } = new();
    public EmbeddingConfig Embedding { get; init; } = new();
    public OutputConfig Output { get; init; } = new();
    public StorageConfig Storage { get; init; } = new();
    public LinkFollowingConfig LinkFollowing { get; init; } = new();
    public Dictionary<string, string> Vibes { get; init; } = new();
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

public record OllamaConfig
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen3:8b";
    public string SentinelModel { get; init; } = "llama3.2:1b";
    public string EmbedModel { get; init; } = "nomic-embed-text";
    public double Temperature { get; init; } = 0.4;
    public int TimeoutSeconds { get; init; } = 300;
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

// JSON serialization context for AOT
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DoomConfig))]
[JsonSerializable(typeof(SourcesConfig))]
[JsonSerializable(typeof(HackerNewsConfig))]
[JsonSerializable(typeof(RedditConfig))]
[JsonSerializable(typeof(WebsiteConfig))]
[JsonSerializable(typeof(OllamaConfig))]
[JsonSerializable(typeof(EmbeddingConfig))]
[JsonSerializable(typeof(OutputConfig))]
[JsonSerializable(typeof(StorageConfig))]
[JsonSerializable(typeof(LinkFollowingConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class DoomConfigContext : JsonSerializerContext;

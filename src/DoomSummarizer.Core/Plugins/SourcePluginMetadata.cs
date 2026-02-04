namespace DoomSummarizer.Plugins;

/// <summary>
///     Describes a source plugin's identity and capabilities.
/// </summary>
public record SourcePluginMetadata
{
    /// <summary>Primary key used for lookup (e.g. "hn").</summary>
    public required string PrimaryKey { get; init; }

    /// <summary>All keys this plugin responds to (includes <see cref="PrimaryKey" />).</summary>
    public required IReadOnlyList<string> Keys { get; init; }

    /// <summary>Human-readable name (e.g. "Hacker News").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Short description for the sources listing.</summary>
    public required string Description { get; init; }

    /// <summary>What this plugin can do.</summary>
    public SourceCapabilities Capabilities { get; init; }

    /// <summary>API key names this plugin needs (empty = no auth required).</summary>
    public IReadOnlyList<string> RequiredApiKeys { get; init; } = [];

    /// <summary>NuGet package ID when distributed as a separate package.</summary>
    public string? PackageId { get; init; }

    /// <summary>Sub-source scopes (e.g. subreddits, news categories).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Usage examples for the sources listing (e.g. "-s hn", "-s reddit:dotnet").</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}

/// <summary>
///     Describes an output plugin's identity and capabilities.
/// </summary>
public record OutputPluginMetadata
{
    /// <summary>Primary key used for lookup (e.g. "smtp").</summary>
    public required string PrimaryKey { get; init; }

    /// <summary>All keys this plugin responds to.</summary>
    public required IReadOnlyList<string> Keys { get; init; }

    /// <summary>Human-readable name (e.g. "SMTP Email").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Short description.</summary>
    public required string Description { get; init; }

    /// <summary>API key names this plugin needs.</summary>
    public IReadOnlyList<string> RequiredApiKeys { get; init; } = [];

    /// <summary>NuGet package ID when distributed as a separate package.</summary>
    public string? PackageId { get; init; }
}

/// <summary>
///     Source plugin capability flags.
/// </summary>
[Flags]
public enum SourceCapabilities
{
    None = 0,

    /// <summary>Supports keyword/query search.</summary>
    Search = 1,

    /// <summary>Supports browsing by topic or category.</summary>
    TopicBrowse = 2,

    /// <summary>Supports RSS/Atom feed consumption.</summary>
    Feed = 4,

    /// <summary>No authentication required.</summary>
    NoAuth = 8,

    /// <summary>Requires an API key or credentials.</summary>
    RequiresAuth = 16,

    /// <summary>Supports sub-source routing (e.g. subreddit, category).</summary>
    SubSource = 32,

    /// <summary>Reads local files instead of network resources.</summary>
    LocalFiles = 64,

    /// <summary>Primarily news content.</summary>
    NewsOnly = 128
}
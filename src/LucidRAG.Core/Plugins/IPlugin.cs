namespace LucidRAG.Plugins;

/// <summary>
///     Minimal plugin interface. The plugin identifies itself and optionally
///     registers additional services. All capabilities are discovered from
///     the assembly via:
///     - IWave implementations (waves)
///     - *.wave.yaml embedded resources (manifests)
///     - ViewComponents (UI)
///     - *.prompt.yaml (prompts)
///     - *.lens.yaml (lenses)
///     - plugin.yaml (metadata)
/// </summary>
public interface IPlugin
{
    /// <summary>
    ///     Plugin metadata (name, version, description, author).
    /// </summary>
    PluginManifest Manifest { get; }

    /// <summary>
    ///     Called once during startup. Register any additional services.
    ///     Most plugins don't need this — assembly scanning handles waves, prompts, lenses.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}

/// <summary>
///     Plugin metadata loaded from plugin.yaml or provided programmatically.
/// </summary>
public record PluginManifest
{
    /// <summary>
    ///     Unique plugin identifier (e.g., "knowledgebase", "postgres", "ollama").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Display name for UI (e.g., "Knowledge Base", "PostgreSQL").
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     Plugin description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Semantic version (e.g., "1.0.0").
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    ///     Author/maintainer.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    ///     License identifier (e.g., "MIT").
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    ///     Tags for categorization and discovery.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    ///     Required plugin dependencies (other plugin names).
    /// </summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>
    ///     Suggested plugins that enhance functionality.
    /// </summary>
    public IReadOnlyList<string> Suggests { get; init; } = [];
}
using DoomSummarizer.Models;

namespace DoomSummarizer.Plugins;

/// <summary>
/// Contract for a source data plugin.
/// Each plugin knows how to fetch <see cref="ContentItem"/>s from one or more related sources.
/// </summary>
public interface ISourcePlugin
{
    /// <summary>Describes the plugin: keys, display name, capabilities.</summary>
    SourcePluginMetadata Metadata { get; }

    /// <summary>
    /// One-time initialization. Called once after registration, before any <see cref="FetchAsync"/> calls.
    /// Use to validate API keys, warm caches, etc.
    /// </summary>
    Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default);

    /// <summary>
    /// Fetch content items for the given context.
    /// The <see cref="SourceFetchContext.SourceKey"/> will be one of this plugin's registered keys.
    /// </summary>
    Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default);
}

/// <summary>
/// Contract for an output plugin (email, file, webhook, etc.).
/// </summary>
public interface IOutputPlugin
{
    /// <summary>Describes the output plugin: key, display name, capabilities.</summary>
    OutputPluginMetadata Metadata { get; }

    /// <summary>One-time initialization.</summary>
    Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default);

    /// <summary>
    /// Send/write output content.
    /// </summary>
    Task SendAsync(OutputContext context, CancellationToken ct = default);
}

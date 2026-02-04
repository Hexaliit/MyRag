using Spectre.Console.Cli;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Optional companion to <see cref="IProcessorPlugin" />. Implement this to contribute
///     CLI commands (e.g. "doomsummarizer books split ...").
///     A plugin can implement <see cref="IProcessorPlugin" /> alone (processing/extension detection only)
///     or both <see cref="IProcessorPlugin" /> + <see cref="ICliPlugin" /> (processing + CLI commands).
/// </summary>
public interface ICliPlugin
{
    /// <summary>CLI metadata for help display.</summary>
    PluginCliMetadata CliMetadata { get; }

    /// <summary>
    ///     Register this plugin's CLI commands with the Spectre.Console configurator.
    ///     Typically adds a branch: config.AddBranch("video", v => { v.AddCommand... })
    /// </summary>
    void ConfigureCommands(IConfigurator config);
}

/// <summary>
///     Describes a CLI plugin's command surface for --help and discovery.
/// </summary>
public record PluginCliMetadata
{
    /// <summary>CLI command name (e.g. "books", "video", "image").</summary>
    public required string CommandName { get; init; }

    /// <summary>Short description for --help.</summary>
    public required string Description { get; init; }

    /// <summary>Usage examples.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}
using DoomSummarizer.Plugins.Adapters;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Registers all built-in source and output plugins.
///     Called at startup before runtime plugins are loaded.
/// </summary>
public static class BuiltinPlugins
{
    /// <summary>
    ///     Register all built-in source plugins with the registry.
    /// </summary>
    public static void RegisterAllSources(SourcePluginRegistry registry)
    {
        registry.Register(new HackerNewsPlugin());
        registry.Register(new RedditPlugin());
        registry.Register(new GoogleNewsPlugin());
        registry.Register(new SearchPlugin());
        registry.Register(new NewsFeedPlugin());
        registry.Register(new AcademicPlugin());
        registry.Register(new SciencePlugin());
        registry.Register(new ReferencePlugin());
        registry.Register(new WebPlugin());
        registry.Register(new UkGovPlugin());
    }

    /// <summary>
    ///     Register all built-in output plugins with the registry.
    /// </summary>
    public static void RegisterAllOutputs(OutputPluginRegistry registry)
    {
        // Output plugins will be added as separate packages (Output.Smtp, Output.SendGrid, etc.)
    }
}
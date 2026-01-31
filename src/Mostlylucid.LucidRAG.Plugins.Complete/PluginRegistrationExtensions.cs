using DoomSummarizer.Plugins;

namespace Mostlylucid.LucidRAG.Plugins.Complete;

/// <summary>
/// Convenience extensions to register all processor plugins at once.
/// </summary>
public static class PluginRegistrationExtensions
{
    /// <summary>
    /// Register all bundled processor plugins with the registry.
    /// </summary>
    public static ProcessorPluginRegistry AddAllPlugins(this ProcessorPluginRegistry registry)
    {
        registry.Register(new DoomSummarizer.Plugin.Books.BookProcessorPlugin());
        registry.Register(new DoomSummarizer.Plugin.Video.VideoProcessorPlugin());
        registry.Register(new DoomSummarizer.Plugin.Image.ImageProcessorPlugin());
        registry.Register(new DoomSummarizer.Plugin.Audio.AudioProcessorPlugin());
        registry.Register(new DoomSummarizer.Plugin.Data.DataProcessorPlugin());
        return registry;
    }
}

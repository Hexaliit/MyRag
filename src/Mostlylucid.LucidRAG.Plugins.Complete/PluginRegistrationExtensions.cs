using DoomSummarizer.Plugins;
using Mostlylucid.DoomSummarizer.Plugin.Audio;
using Mostlylucid.DoomSummarizer.Plugin.Books;
using Mostlylucid.DoomSummarizer.Plugin.Data;
using Mostlylucid.DoomSummarizer.Plugin.Image;
using Mostlylucid.DoomSummarizer.Plugin.Video;

namespace Mostlylucid.LucidRAG.Plugins.Complete;

/// <summary>
///     Convenience extensions to register all processor plugins at once.
/// </summary>
public static class PluginRegistrationExtensions
{
    /// <summary>
    ///     Register all bundled processor plugins with the registry.
    /// </summary>
    public static ProcessorPluginRegistry AddAllPlugins(this ProcessorPluginRegistry registry)
    {
        registry.Register(new BookProcessorPlugin());
        registry.Register(new VideoProcessorPlugin());
        registry.Register(new ImageProcessorPlugin());
        registry.Register(new AudioProcessorPlugin());
        registry.Register(new DataProcessorPlugin());
        return registry;
    }
}
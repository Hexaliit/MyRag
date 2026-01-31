using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Mostlylucid.DoomSummarizer.Plugin.Video.Commands;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Video;

public class VideoProcessorPlugin : IProcessorPlugin, ICliPlugin
{
    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "video",
        DisplayName = "Video Analyzer",
        Description = "Shot detection, scene segmentation, and transcription for video files",
        Version = "1.0.0",
        SupportedExtensions = [".mp4", ".mkv", ".avi", ".webm", ".mov", ".wmv"],
        MinActivationWords = 0,
        DocumentTypes = ["video"]
    };

    public PluginCliMetadata CliMetadata { get; } = new()
    {
        CommandName = "video",
        Description = "Video analysis: shot detection, scene segmentation, transcription",
        Examples = ["video analyze movie.mp4", "video shots trailer.mp4", "video scenes episode.mkv"]
    };

    public void ConfigureCommands(IConfigurator config)
    {
        config.AddBranch("video", video =>
        {
            video.SetDescription(CliMetadata.Description);
            video.AddCommand<VideoAnalyzeCommand>("analyze")
                .WithDescription("Run full video analysis pipeline")
                .WithExample("video", "analyze", "movie.mp4");
            video.AddCommand<VideoShotsCommand>("shots")
                .WithDescription("Detect shot boundaries")
                .WithExample("video", "shots", "trailer.mp4");
            video.AddCommand<VideoScenesCommand>("scenes")
                .WithDescription("Segment video into scenes")
                .WithExample("video", "scenes", "episode.mkv");
        });
    }

    public IReadOnlyList<IDocumentReader> Readers => [];
    public IReadOnlyList<IDocumentSplitter> Splitters => [];
    public IReadOnlyList<TemplateDefinition> Templates => [];

    public Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default)
        => Task.CompletedTask;

    public bool CanProcess(string markdown, ProcessingContext context)
    {
        if (context.FileName == null) return false;
        var ext = Path.GetExtension(context.FileName);
        return Metadata.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ProcessorResult> ProcessAsync(string markdown, ProcessorOptions options, CancellationToken ct = default)
        => throw new NotSupportedException("Video processing uses IPipeline. Use CLI commands for direct video analysis.");
}

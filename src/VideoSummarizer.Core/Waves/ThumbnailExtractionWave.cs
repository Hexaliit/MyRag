using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Extracts low-resolution thumbnails for fast deduplication.
///     Uses 128px wide thumbnails for perceptual hash comparison.
///     Emits: keyframes.thumbnails_extracted
/// </summary>
public class ThumbnailExtractionWave : IVideoWave, ISignalAwareVideoWave
{
    private const int ThumbnailWidth = 128;
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<ThumbnailExtractionWave> _logger;

    public ThumbnailExtractionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<ThumbnailExtractionWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.KeyframesSelected];
    public IReadOnlyList<string> OptionalSignals => [];
    public IReadOnlyList<string> EmittedSignals => [VideoSignals.ThumbnailsExtracted];
    public IReadOnlyList<string> CacheEmits => ["thumbnails"];
    public IReadOnlyList<string> CacheUses => ["keyframe_selections"];

    public string Name => "thumbnail_extraction";
    public int Priority => 830; // After keyframe selection
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual];

    public bool ShouldRun(VideoContext context)
    {
        return context.GetCached<List<KeyframeSelection>>("keyframe_selections")?.Count > 5;
        // Only if enough frames
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Extracting thumbnails for deduplication", 0);

        var selections = context.GetCached<List<KeyframeSelection>>("keyframe_selections")!;
        var thumbDir = Path.Combine(context.WorkingDirectory, "keyframes_thumb");
        Directory.CreateDirectory(thumbDir);

        // Extract small thumbnails (128px wide) - much faster than full-res
        var thumbnails = await _ffmpegService.ExtractFramesAtTimestampsAsync(
            context.VideoPath,
            selections.Select(k => k.Timestamp),
            thumbDir,
            ct,
            "thumb_",
            ThumbnailWidth);

        _logger.LogInformation("Extracted {Count} thumbnails for deduplication", thumbnails.Count);

        // Cache thumbnails for deduplication wave
        context.SetCached("thumbnails", thumbnails);

        // Emit signal
        context.AddSignal(new VideoSignal
        {
            Key = VideoSignals.ThumbnailsExtracted,
            Value = thumbnails.Count,
            Source = Name,
            Tags = [VideoSignalTags.Visual]
        });

        context.ReportProgress("Thumbnail extraction complete", 100);
    }
}
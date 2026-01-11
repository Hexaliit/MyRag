using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Extracts full-resolution keyframes for unique (non-duplicate) frames.
/// Only runs after deduplication to minimize extraction work.
/// Emits: keyframes.extracted
/// </summary>
public class FullResExtractionWave : IVideoWave, ISignalAwareVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<FullResExtractionWave> _logger;

    public string Name => "fullres_extraction";
    public int Priority => 810; // After deduplication
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual];

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.KeyframesDeduplicated];
    public IReadOnlyList<string> OptionalSignals => [VideoSignals.KeyframesSelected]; // Fallback if no dedup
    public IReadOnlyList<string> EmittedSignals => [
        VideoSignals.KeyframesExtracted,
        VideoSignals.KeyframesCount
    ];
    public IReadOnlyList<string> CacheEmits => ["extracted_frames"];
    public IReadOnlyList<string> CacheUses => ["unique_timestamps", "keyframe_selections"];

    public FullResExtractionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<FullResExtractionWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        context.Metadata != null &&
        (context.GetCached<HashSet<double>>("unique_timestamps") != null ||
         context.GetCached<List<KeyframeSelection>>("keyframe_selections") != null);

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Extracting full-resolution frames", 0);

        var keyframeDir = Path.Combine(context.WorkingDirectory, "keyframes");
        Directory.CreateDirectory(keyframeDir);

        // Get timestamps to extract
        var uniqueTimestamps = context.GetCached<HashSet<double>>("unique_timestamps");
        var timestamps = uniqueTimestamps != null
            ? uniqueTimestamps.ToList()
            : context.GetCached<List<KeyframeSelection>>("keyframe_selections")?
                .Select(s => s.Timestamp)
                .ToList() ?? new List<double>();

        if (timestamps.Count == 0)
        {
            _logger.LogWarning("No keyframes to extract");
            return;
        }

        // Extract full-res frames
        var extractedFrames = await _ffmpegService.ExtractFramesAtTimestampsAsync(
            context.VideoPath,
            timestamps,
            keyframeDir,
            ct);

        _logger.LogInformation("Extracted {Count} full-resolution keyframes", extractedFrames.Count);

        // Store frame paths in context
        foreach (var (timestamp, path) in extractedFrames)
        {
            var frameIndex = (int)(timestamp * context.Metadata!.Fps);
            context.Keyframes[frameIndex] = path;
            context.FrameTimestamps[frameIndex] = timestamp;
        }

        // Cache for downstream
        context.SetCached("extracted_frames", extractedFrames);

        // Emit signals
        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.KeyframesExtracted,
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = VideoSignals.KeyframesCount,
                Value = extractedFrames.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        context.ReportProgress("Full-res extraction complete", 100);
    }
}

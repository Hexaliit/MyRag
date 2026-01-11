using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Detects codec I-frames from video stream.
/// This is a fast operation that doesn't require decoding.
/// Emits: keyframes.iframes_detected
/// </summary>
public class IFrameDetectionWave : IVideoWave, ISignalAwareVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<IFrameDetectionWave> _logger;

    public string Name => "iframe_detection";
    public int Priority => 850; // After shot detection, before keyframe selection
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, VideoSignalTags.Shot];

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.ShotsDetected];
    public IReadOnlyList<string> OptionalSignals => [];
    public IReadOnlyList<string> EmittedSignals => [
        VideoSignals.IframesDetected,
        VideoSignals.IframesCount
    ];
    public IReadOnlyList<string> CacheEmits => ["iframes"];
    public IReadOnlyList<string> CacheUses => [];

    public IFrameDetectionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<IFrameDetectionWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        context.Metadata != null && context.Shots.Count > 0;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        context.ReportProgress("Detecting codec I-frames", 0);

        // Emit wave started signal
        context.AddSignal(new VideoSignal
        {
            Key = $"{Name}.started",
            Value = DateTimeOffset.UtcNow,
            Source = Name,
            Tags = [VideoSignalTags.Visual, "wave", "flow"]
        });

        var videoPath = context.VideoPath;

        // Extract codec-level I-frames (fast - no decoding needed)
        var iframes = await _ffmpegService.ExtractIFramesAsync(videoPath, ct);

        _logger.LogInformation("Detected {Count} codec I-frames", iframes.Count);

        // Cache I-frames for downstream waves
        context.SetCached("iframes", iframes);

        // Emit signals
        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.IframesDetected,
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = VideoSignals.IframesCount,
                Value = iframes.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        sw.Stop();

        // Emit wave completed signal with timing
        context.AddSignals([
            new VideoSignal
            {
                Key = $"{Name}.completed",
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "wave", "flow"]
            },
            new VideoSignal
            {
                Key = $"{Name}.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "timing", "persist"]
            }
        ]);

        context.ReportProgress("I-frame detection complete", 100);
    }
}

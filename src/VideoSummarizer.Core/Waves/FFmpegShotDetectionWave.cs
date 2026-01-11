using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// GPU-accelerated shot detection using FFmpeg's scene filter with NVDEC.
/// Much faster than OpenCV-based detection (5-10x on NVIDIA GPUs).
/// Emits: shots.detected, shots.count
/// </summary>
public class FFmpegShotDetectionWave : IVideoWave, ISignalAwareVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<FFmpegShotDetectionWave> _logger;

    // Detection threshold (0.0-1.0, higher = fewer cuts detected)
    private const double SceneThreshold = 0.3;

    public string Name => "ffmpeg_shot_detection";
    public int Priority => 900; // Same as OpenCV version
    public IReadOnlyList<string> Tags => [VideoSignalTags.Shot, VideoSignalTags.Visual];

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.VideoNormalized];
    public IReadOnlyList<string> OptionalSignals => [];
    public IReadOnlyList<string> EmittedSignals => [
        VideoSignals.ShotsDetected,
        VideoSignals.ShotsCount,
        VideoSignals.ShotsAvgDuration,
        VideoSignals.ShotsDetectionMethod
    ];
    public IReadOnlyList<string> CacheEmits => [];
    public IReadOnlyList<string> CacheUses => [];

    public FFmpegShotDetectionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<FFmpegShotDetectionWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) => context.Metadata != null;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        context.ReportProgress("Detecting shots (FFmpeg GPU)", 0);

        // Emit wave started signal
        context.AddSignal(new VideoSignal
        {
            Key = $"{Name}.started",
            Value = DateTimeOffset.UtcNow,
            Source = Name,
            Tags = [VideoSignalTags.Shot, "wave", "flow"]
        });

        var videoPath = context.VideoPath;
        var metadata = context.Metadata!;
        var fps = metadata.Fps;

        _logger.LogInformation("Using FFmpeg GPU-accelerated scene detection");

        // Detect scene changes using FFmpeg (with NVDEC if available)
        context.ReportProgress("Running FFmpeg scene filter", 10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sceneChanges = await _ffmpegService.DetectSceneChangesAsync(videoPath, SceneThreshold, ct);
        sw.Stop();

        // Emit operation timing signal
        context.AddSignal(new VideoSignal
        {
            Key = "shots.scene_filter_ms",
            Value = sw.ElapsedMilliseconds,
            Source = Name,
            Tags = [VideoSignalTags.Shot, "timing"]
        });

        _logger.LogInformation("FFmpeg scene detection completed in {Time}ms, found {Count} changes",
            sw.ElapsedMilliseconds, sceneChanges.Count);

        // Also get black frames for better segmentation
        context.ReportProgress("Detecting black frames", 40);
        var blackFrames = await _ffmpegService.DetectBlackFramesAsync(videoPath, 0.5, 0.1, ct);

        _logger.LogInformation("Found {Count} black frame segments", blackFrames.Count);

        // Merge scene changes with black frame boundaries
        context.ReportProgress("Building shot segments", 60);
        var allBoundaries = sceneChanges
            .Select(s => (Timestamp: s.Timestamp, Confidence: s.Confidence, Type: CutType.HardCut))
            .Concat(blackFrames.Select(b => (Timestamp: b.StartTime, Confidence: 0.8, Type: CutType.Fade)))
            .OrderBy(b => b.Timestamp)
            .ToList();

        // Filter boundaries too close together
        var filteredBoundaries = FilterCloseBoundaries(allBoundaries, minGap: 0.5);

        // Create shots
        context.ReportProgress("Creating shots", 80);
        CreateShots(context, filteredBoundaries, fps);

        // Add signals - using well-known signal keys for coordination
        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.ShotsDetected,
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Shot]
            },
            new VideoSignal
            {
                Key = VideoSignals.ShotsCount,
                Value = context.Shots.Count,
                Source = Name,
                Tags = [VideoSignalTags.Shot]
            },
            new VideoSignal
            {
                Key = VideoSignals.ShotsAvgDuration,
                Value = context.Shots.Count > 0 ? context.Shots.Average(s => s.EndTime - s.StartTime) : 0,
                Source = Name,
                Tags = [VideoSignalTags.Shot]
            },
            new VideoSignal
            {
                Key = VideoSignals.ShotsDetectionMethod,
                Value = "ffmpeg_gpu",
                Source = Name,
                Tags = [VideoSignalTags.Shot]
            },
            new VideoSignal
            {
                Key = "shots.detection_time_ms",
                Value = sw.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Shot]
            }
        ]);

        totalSw.Stop();

        // Emit wave completed signal with timing
        context.AddSignals([
            new VideoSignal
            {
                Key = $"{Name}.completed",
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Shot, "wave", "flow"]
            },
            new VideoSignal
            {
                Key = $"{Name}.duration_ms",
                Value = totalSw.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Shot, "timing", "persist"]
            }
        ]);

        context.ReportProgress("Shot detection complete", 100);
    }

    private List<(double Timestamp, double Confidence, CutType Type)> FilterCloseBoundaries(
        List<(double Timestamp, double Confidence, CutType Type)> boundaries,
        double minGap)
    {
        var filtered = new List<(double, double, CutType)>();
        double lastTime = -minGap * 2;

        foreach (var boundary in boundaries)
        {
            if (boundary.Timestamp - lastTime >= minGap)
            {
                filtered.Add(boundary);
                lastTime = boundary.Timestamp;
            }
            else if (boundary.Confidence > filtered.LastOrDefault().Item2)
            {
                // Replace with higher confidence boundary
                filtered[^1] = boundary;
                lastTime = boundary.Timestamp;
            }
        }

        return filtered;
    }

    private void CreateShots(
        VideoContext context,
        List<(double Timestamp, double Confidence, CutType Type)> boundaries,
        double fps)
    {
        var videoId = context.Metadata!.Id;
        var duration = context.Metadata.Duration;
        var lastTime = 0.0;

        for (int i = 0; i <= boundaries.Count; i++)
        {
            var startTime = lastTime;
            var endTime = i < boundaries.Count ? boundaries[i].Timestamp : duration;

            if (endTime - startTime < 0.1) continue; // Skip very short shots

            var cutType = i < boundaries.Count ? boundaries[i].Type : CutType.Unknown;
            var confidence = i < boundaries.Count ? boundaries[i].Confidence : 1.0;

            // Estimate keyframe index (middle of shot)
            var keyframeTime = (startTime + endTime) / 2;
            var keyframeIndex = (int)(keyframeTime * fps);

            var shot = new ShotSegment
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                StartTime = startTime,
                EndTime = endTime,
                KeyframeIndex = keyframeIndex,
                CutType = cutType,
                MotionScore = 0.5, // Not computed in FFmpeg mode
                Confidence = confidence
            };

            context.Shots.Add(shot);
            context.FrameTimestamps[keyframeIndex] = keyframeTime;

            lastTime = endTime;
        }

        _logger.LogInformation("Created {Count} shots", context.Shots.Count);
    }
}

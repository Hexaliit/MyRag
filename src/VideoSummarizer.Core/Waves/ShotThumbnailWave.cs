using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Stage 2.5: Shot thumbnail extraction.
/// Ensures every shot has a representative thumbnail image.
/// Extracts frames at shot midpoints for shots without keyframes.
/// </summary>
public class ShotThumbnailWave : IVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<ShotThumbnailWave> _logger;

    // Configuration
    private const int MaxThumbnailsToExtract = 200; // Limit for very long videos
    private const int ThumbnailWidth = 320; // Thumbnail width (aspect ratio preserved)

    public string Name => "shot_thumbnails";
    public int Priority => 790; // After keyframe extraction (800), before subtitle extraction (750)
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, VideoSignalTags.Shot];

    public ShotThumbnailWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<ShotThumbnailWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        context.Metadata != null && context.Shots.Count > 0;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Extracting shot thumbnails", 0);

        var videoPath = context.VideoPath;
        var thumbnailDir = Path.Combine(context.WorkingDirectory, "thumbnails");
        Directory.CreateDirectory(thumbnailDir);

        // Find shots without thumbnails
        var shotsNeedingThumbnails = context.Shots
            .Where(shot => !context.Keyframes.ContainsKey(shot.KeyframeIndex))
            .Take(MaxThumbnailsToExtract)
            .ToList();

        if (shotsNeedingThumbnails.Count == 0)
        {
            _logger.LogInformation("All {Count} shots already have keyframes", context.Shots.Count);
            context.ReportProgress("Shot thumbnails complete", 100);
            return;
        }

        _logger.LogInformation(
            "Extracting thumbnails for {NeedThumbs}/{TotalShots} shots without keyframes",
            shotsNeedingThumbnails.Count,
            context.Shots.Count);

        // Extract timestamps for thumbnail extraction (midpoint of each shot)
        var timestamps = shotsNeedingThumbnails
            .Select(shot => (shot.StartTime + shot.EndTime) / 2)
            .ToList();

        // Extract all thumbnails in a single FFmpeg pass (efficient)
        context.ReportProgress("Extracting thumbnail images", 20);
        var extractedFrames = await _ffmpegService.ExtractFramesAtTimestampsAsync(
            videoPath,
            timestamps,
            thumbnailDir,
            ct,
            prefix: "thumb_",
            width: ThumbnailWidth);

        _logger.LogInformation("Extracted {Count} shot thumbnails", extractedFrames.Count);

        // Map extracted thumbnails back to shots
        context.ReportProgress("Mapping thumbnails to shots", 80);
        var timestampToShot = shotsNeedingThumbnails
            .ToDictionary(
                shot => (shot.StartTime + shot.EndTime) / 2,
                shot => shot);

        foreach (var (timestamp, path) in extractedFrames)
        {
            // Find the shot this thumbnail belongs to (nearest timestamp)
            var closestTimestamp = timestampToShot.Keys
                .OrderBy(t => Math.Abs(t - timestamp))
                .FirstOrDefault();

            if (Math.Abs(closestTimestamp - timestamp) < 1.0) // Within 1 second tolerance
            {
                var shot = timestampToShot[closestTimestamp];
                var frameIndex = shot.KeyframeIndex;

                // Store thumbnail as keyframe
                context.Keyframes[frameIndex] = path;
                context.FrameTimestamps[frameIndex] = timestamp;

                // Cache the fact this is a thumbnail (not full analysis keyframe)
                context.SetCached($"is_thumbnail.{frameIndex}", true);
            }
        }

        // Add summary signals
        context.AddSignals([
            new VideoSignal
            {
                Key = "thumbnails.extracted",
                Value = extractedFrames.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual, VideoSignalTags.Shot]
            },
            new VideoSignal
            {
                Key = "thumbnails.shots_covered",
                Value = context.Shots.Count(s => context.Keyframes.ContainsKey(s.KeyframeIndex)),
                Source = Name,
                Tags = [VideoSignalTags.Visual, VideoSignalTags.Shot]
            }
        ]);

        context.ReportProgress("Shot thumbnails complete", 100);
    }
}

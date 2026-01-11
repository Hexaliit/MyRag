using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Stage 2.6: Extract chapter marks and create preliminary scene segments.
/// Chapters from video containers provide high-confidence scene boundaries.
/// Also detects black frames and silence as potential segment markers.
/// </summary>
public class ChapterExtractionWave : IVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<ChapterExtractionWave> _logger;

    public string Name => "chapter_extraction";
    public int Priority => 740; // After subtitles
    public IReadOnlyList<string> Tags => [VideoSignalTags.Scene, VideoSignalTags.Metadata];

    public ChapterExtractionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<ChapterExtractionWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) => context.Metadata != null;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Extracting chapters", 0);

        var videoPath = context.VideoPath;

        // 1. Extract embedded chapters from container
        context.ReportProgress("Reading chapter markers", 10);
        var chapters = await _ffmpegService.ExtractChaptersAsync(videoPath, ct);

        _logger.LogInformation("Found {Count} embedded chapters", chapters.Count);

        // Store chapters in context for scene clustering
        context.SetCached("chapters", chapters);

        // Create scene segments from chapters (high confidence)
        foreach (var chapter in chapters)
        {
            var scene = new SceneSegment
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata!.Id,
                StartTime = chapter.StartTime,
                EndTime = chapter.EndTime,
                Label = chapter.Title,
                Confidence = 0.95 // Embedded chapters are reliable
            };

            // Find shots that belong to this scene
            scene.ShotIds.AddRange(
                context.Shots
                    .Where(s => s.StartTime >= chapter.StartTime && s.EndTime <= chapter.EndTime)
                    .Select(s => s.Id));

            // Add keyframe indices from those shots
            scene.KeyframeIndices.AddRange(
                context.Shots
                    .Where(s => scene.ShotIds.Contains(s.Id))
                    .Select(s => s.KeyframeIndex));

            context.Scenes.Add(scene);

            context.AddSignal(new VideoSignal
            {
                Key = $"chapter.{chapter.Index}",
                Value = chapter.Title ?? $"Chapter {chapter.Index + 1}",
                Source = Name,
                StartTime = chapter.StartTime,
                EndTime = chapter.EndTime,
                Confidence = 0.95,
                Tags = [VideoSignalTags.Scene]
            });
        }

        // 2. Detect black frames (segment boundaries)
        context.ReportProgress("Detecting black frames", 40);
        var blackFrames = await _ffmpegService.DetectBlackFramesAsync(videoPath, ct: ct);

        _logger.LogInformation("Found {Count} black frame segments", blackFrames.Count);

        context.SetCached("black_frames", blackFrames);

        foreach (var bf in blackFrames)
        {
            context.AddSignal(new VideoSignal
            {
                Key = "black_frame",
                Value = bf.Duration,
                Source = Name,
                StartTime = bf.StartTime,
                EndTime = bf.EndTime,
                Confidence = 0.8,
                Tags = [VideoSignalTags.Visual, VideoSignalTags.Scene]
            });
        }

        // 3. Detect silence (audio boundaries)
        context.ReportProgress("Detecting silence", 60);
        var silences = await _ffmpegService.DetectSilenceAsync(videoPath, ct: ct);

        _logger.LogInformation("Found {Count} silence segments", silences.Count);

        context.SetCached("silences", silences);

        foreach (var silence in silences)
        {
            context.AddSignal(new VideoSignal
            {
                Key = "silence",
                Value = silence.Duration,
                Source = Name,
                StartTime = silence.StartTime,
                EndTime = silence.EndTime,
                Confidence = 0.7,
                Tags = [VideoSignalTags.Audio, VideoSignalTags.Scene]
            });
        }

        // 4. Analyze audio loudness
        context.ReportProgress("Analyzing loudness", 80);
        var loudness = await _ffmpegService.AnalyzeLoudnessAsync(videoPath, ct);

        if (loudness != null)
        {
            context.SetCached("loudness", loudness);

            context.AddSignals([
                new VideoSignal
                {
                    Key = "audio.loudness_lufs",
                    Value = loudness.IntegratedLoudness,
                    Source = Name,
                    Tags = [VideoSignalTags.Audio, VideoSignalTags.Quality]
                },
                new VideoSignal
                {
                    Key = "audio.true_peak",
                    Value = loudness.TruePeak,
                    Source = Name,
                    Tags = [VideoSignalTags.Audio, VideoSignalTags.Quality]
                },
                new VideoSignal
                {
                    Key = "audio.loudness_range",
                    Value = loudness.LoudnessRange,
                    Source = Name,
                    Tags = [VideoSignalTags.Audio]
                }
            ]);

            _logger.LogInformation("Audio loudness: {LUFS} LUFS, peak: {Peak} dBFS",
                loudness.IntegratedLoudness, loudness.TruePeak);
        }

        // 5. Infer additional scene boundaries from black frames + silence
        if (chapters.Count == 0)
        {
            context.ReportProgress("Inferring scene boundaries", 90);
            InferSceneBoundaries(context, blackFrames, silences);
        }

        // Add summary signals
        context.AddSignals([
            new VideoSignal
            {
                Key = "chapter.count",
                Value = chapters.Count,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "black_frame.count",
                Value = blackFrames.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "silence.count",
                Value = silences.Count,
                Source = Name,
                Tags = [VideoSignalTags.Audio]
            }
        ]);

        context.ReportProgress("Chapter extraction complete", 100);
    }

    /// <summary>
    /// Infer scene boundaries from black frames and silence when no chapters exist.
    /// Black frame + silence = likely scene boundary.
    /// </summary>
    private void InferSceneBoundaries(
        VideoContext context,
        List<BlackFrameInfo> blackFrames,
        List<SilenceInfo> silences)
    {
        if (blackFrames.Count == 0 && silences.Count == 0) return;

        // Find where black frames and silence overlap (strong boundary signal)
        var boundaries = new List<(double Time, double Confidence)>();

        foreach (var bf in blackFrames)
        {
            var midpoint = (bf.StartTime + bf.EndTime) / 2;

            // Check if silence overlaps with this black frame
            var hasOverlappingSilence = silences.Any(s =>
                s.StartTime < bf.EndTime && s.EndTime > bf.StartTime);

            var confidence = hasOverlappingSilence ? 0.85 : 0.6;
            boundaries.Add((midpoint, confidence));
        }

        // Add silence-only boundaries (weaker signal)
        foreach (var silence in silences)
        {
            var midpoint = (silence.StartTime + silence.EndTime) / 2;

            // Skip if already covered by a black frame boundary
            if (boundaries.Any(b => Math.Abs(b.Time - midpoint) < 2.0))
                continue;

            // Only add if silence is significant (> 2 seconds)
            if (silence.Duration >= 2.0)
            {
                boundaries.Add((midpoint, 0.5));
            }
        }

        // Sort and filter boundaries
        var sortedBoundaries = boundaries
            .OrderBy(b => b.Time)
            .ToList();

        // Minimum scene duration: 10 seconds
        var filteredBoundaries = new List<(double Time, double Confidence)>();
        var lastBoundary = 0.0;

        foreach (var (time, confidence) in sortedBoundaries)
        {
            if (time - lastBoundary >= 10.0)
            {
                filteredBoundaries.Add((time, confidence));
                lastBoundary = time;
            }
        }

        // Create scene segments from boundaries
        var videoDuration = context.Metadata!.Duration;
        var sceneStart = 0.0;

        foreach (var (boundaryTime, confidence) in filteredBoundaries)
        {
            if (boundaryTime <= sceneStart) continue;

            var scene = new SceneSegment
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata.Id,
                StartTime = sceneStart,
                EndTime = boundaryTime,
                Confidence = confidence,
                Label = null // Will be labeled by scene clustering wave
            };

            // Associate shots
            scene.ShotIds.AddRange(
                context.Shots
                    .Where(s => s.StartTime >= sceneStart && s.EndTime <= boundaryTime)
                    .Select(s => s.Id));

            context.Scenes.Add(scene);
            sceneStart = boundaryTime;
        }

        // Add final scene
        if (sceneStart < videoDuration - 5.0)
        {
            var finalScene = new SceneSegment
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata.Id,
                StartTime = sceneStart,
                EndTime = videoDuration,
                Confidence = 0.5
            };

            finalScene.ShotIds.AddRange(
                context.Shots
                    .Where(s => s.StartTime >= sceneStart)
                    .Select(s => s.Id));

            context.Scenes.Add(finalScene);
        }

        _logger.LogInformation("Inferred {Count} scene boundaries from A/V signals",
            filteredBoundaries.Count);
    }
}

using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Selects optimal keyframes based on shots and I-frame positions.
///     Prioritizes: shot keyframes, codec I-frames aligned with shots, regular intervals.
///     Emits: keyframes.selected
/// </summary>
public class KeyframeSelectionWave : IVideoWave, ISignalAwareVideoWave
{
    // Configuration
    private const int MaxKeyframesToProcess = 50;
    private const double MinKeyframeInterval = 2.0;
    private readonly ILogger<KeyframeSelectionWave> _logger;

    public KeyframeSelectionWave(ILogger<KeyframeSelectionWave> logger)
    {
        _logger = logger;
    }

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals =>
    [
        VideoSignals.ShotsDetected,
        VideoSignals.IframesDetected
    ];

    public IReadOnlyList<string> OptionalSignals => [];

    public IReadOnlyList<string> EmittedSignals =>
    [
        VideoSignals.KeyframesSelected,
        VideoSignals.KeyframesSelectedCount
    ];

    public IReadOnlyList<string> CacheEmits => ["keyframe_selections"];
    public IReadOnlyList<string> CacheUses => ["iframes"];

    public string Name => "keyframe_selection";
    public int Priority => 840; // After I-frame detection
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, VideoSignalTags.Shot];

    public bool ShouldRun(VideoContext context)
    {
        return context.Metadata != null &&
               context.Shots.Count > 0 &&
               context.GetCached<List<IFrameInfo>>("iframes") != null;
    }

    public Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Selecting keyframes", 0);

        var iframes = context.GetCached<List<IFrameInfo>>("iframes") ?? new List<IFrameInfo>();
        var selections = new List<KeyframeSelection>();
        var usedTimestamps = new HashSet<double>();

        // First pass: use shot keyframes (best quality frame per shot from detection)
        foreach (var shot in context.Shots)
        {
            var shotKeyframeTime = context.FrameTimestamps.GetValueOrDefault(shot.KeyframeIndex, shot.StartTime);

            // Find nearest codec I-frame (might be better quality)
            var nearestIframe = iframes
                .Where(f => Math.Abs(f.Timestamp - shotKeyframeTime) < 1.0)
                .OrderBy(f => Math.Abs(f.Timestamp - shotKeyframeTime))
                .FirstOrDefault();

            var timestamp = nearestIframe?.Timestamp ?? shotKeyframeTime;

            if (!usedTimestamps.Contains(timestamp))
            {
                selections.Add(new KeyframeSelection
                {
                    Timestamp = timestamp,
                    Source = "shot_keyframe",
                    ShotId = shot.Id,
                    FrameIndex = (int)(timestamp * context.Metadata!.Fps)
                });
                usedTimestamps.Add(timestamp);
            }
        }

        // Second pass: add I-frames that aren't near existing keyframes
        foreach (var iframe in iframes)
        {
            if (selections.Count >= MaxKeyframesToProcess) break;

            var tooClose = usedTimestamps.Any(t => Math.Abs(t - iframe.Timestamp) < MinKeyframeInterval);
            if (tooClose) continue;

            selections.Add(new KeyframeSelection
            {
                Timestamp = iframe.Timestamp,
                Source = "codec_iframe",
                FrameIndex = (int)(iframe.Timestamp * context.Metadata!.Fps)
            });
            usedTimestamps.Add(iframe.Timestamp);
        }

        // Sort and limit
        var finalSelections = selections
            .OrderBy(s => s.Timestamp)
            .Take(MaxKeyframesToProcess)
            .ToList();

        _logger.LogInformation("Selected {Count} keyframes from {Shots} shots and {IFrames} I-frames",
            finalSelections.Count, context.Shots.Count, iframes.Count);

        // Cache selections for downstream waves
        context.SetCached("keyframe_selections", finalSelections);

        // Emit signals
        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.KeyframesSelected,
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = VideoSignals.KeyframesSelectedCount,
                Value = finalSelections.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        context.ReportProgress("Keyframe selection complete", 100);
        return Task.CompletedTask;
    }
}

/// <summary>
///     Represents a selected keyframe candidate.
/// </summary>
public record KeyframeSelection
{
    public double Timestamp { get; init; }
    public string Source { get; init; } = "";
    public Guid? ShotId { get; init; }
    public int FrameIndex { get; init; }
}
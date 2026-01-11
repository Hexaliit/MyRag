using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Deduplicates keyframes using perceptual hashing.
/// Filters out visually similar frames to reduce downstream processing.
/// Emits: keyframes.deduplicated
/// </summary>
public class DeduplicationWave : IVideoWave, ISignalAwareVideoWave
{
    private readonly KeyframeDeduplicationService? _deduplicationService;
    private readonly ILogger<DeduplicationWave> _logger;

    private const int HammingThreshold = 10;

    public string Name => "keyframe_deduplication";
    public int Priority => 820; // After thumbnail extraction
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual];

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.ThumbnailsExtracted];
    public IReadOnlyList<string> OptionalSignals => [];
    public IReadOnlyList<string> EmittedSignals => [
        VideoSignals.KeyframesDeduplicated,
        VideoSignals.KeyframesDuplicatesSkipped
    ];
    public IReadOnlyList<string> CacheEmits => ["unique_timestamps", "dhash_values"];
    public IReadOnlyList<string> CacheUses => ["thumbnails", "keyframe_selections"];

    public DeduplicationWave(
        ILogger<DeduplicationWave> logger,
        KeyframeDeduplicationService? deduplicationService = null)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        _deduplicationService != null &&
        context.GetCached<Dictionary<double, string>>("thumbnails")?.Count > 5;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Deduplicating similar frames", 0);

        var thumbnails = context.GetCached<Dictionary<double, string>>("thumbnails")!;
        var selections = context.GetCached<List<KeyframeSelection>>("keyframe_selections")!;

        // Deduplicate based on perceptual hashes
        var uniqueFrames = await _deduplicationService!.FilterSimilarFramesAsync(
            thumbnails, HammingThreshold, ct);

        var uniqueTimestamps = uniqueFrames.Select(f => f.Timestamp).ToHashSet();
        var duplicatesSkipped = selections.Count - uniqueTimestamps.Count;

        _logger.LogInformation(
            "Deduplication: {Original} -> {Unique} frames ({Skipped} similar frames skipped)",
            selections.Count, uniqueTimestamps.Count, duplicatesSkipped);

        // Store dHash values for later use (convert ulong to hex string)
        var dhashValues = new Dictionary<int, string>();
        foreach (var frame in uniqueFrames)
        {
            var frameIndex = (int)(frame.Timestamp * context.Metadata!.Fps);
            var hashStr = frame.DHash.ToString("x16");
            dhashValues[frameIndex] = hashStr;
            context.SetCached($"keyframe_dhash.{frameIndex}", hashStr);
        }

        // Cache results for downstream waves
        context.SetCached("unique_timestamps", uniqueTimestamps);
        context.SetCached("dhash_values", dhashValues);

        // Emit signals
        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.KeyframesDeduplicated,
                Value = uniqueTimestamps.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = VideoSignals.KeyframesDuplicatesSkipped,
                Value = duplicatesSkipped,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        context.ReportProgress("Deduplication complete", 100);
    }
}

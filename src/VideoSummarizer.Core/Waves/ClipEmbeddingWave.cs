using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Capabilities;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Generates CLIP embeddings for keyframes using batch GPU processing.
/// All configuration values come from waves.yaml - NO magic numbers.
/// Uses capability atoms for backpressure and time estimation.
/// Emits: clip.embeddings_ready
/// </summary>
public class ClipEmbeddingWave : IVideoWave, ISignalAwareVideoWave
{
    private readonly BatchClipEmbeddingService? _batchClipService;
    private readonly VideoWaveManifestLoader _manifestLoader;
    private readonly ILogger<ClipEmbeddingWave> _logger;

    // Configuration from YAML
    private int BatchSize => _manifestLoader.GetConfigValue<int>(Name, "batch_size", 8);
    private int MaxConcurrency => _manifestLoader.GetConfigValue<int>(Name, "max_concurrency", 2);
    private int TargetLatencyMs => _manifestLoader.GetConfigValue<int>(Name, "target_latency_ms", 500);

    public string Name => "clip_embedding";
    public int Priority => 800; // After full-res extraction
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual];

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.KeyframesExtracted];
    public IReadOnlyList<string> OptionalSignals => [];
    public IReadOnlyList<string> EmittedSignals => [
        VideoSignals.ClipEmbeddingsReady,
        VideoSignals.ClipEmbeddingsCount,
        VideoSignals.ClipBatchSize
    ];
    public IReadOnlyList<string> CacheEmits => [];
    public IReadOnlyList<string> CacheUses => ["extracted_frames"];

    public ClipEmbeddingWave(
        VideoWaveManifestLoader manifestLoader,
        ILogger<ClipEmbeddingWave> logger,
        BatchClipEmbeddingService? batchClipService = null)
    {
        _manifestLoader = manifestLoader;
        _batchClipService = batchClipService;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        _batchClipService != null &&
        context.Keyframes.Count > 0;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Generating CLIP embeddings (batch GPU)", 0);

        // Prepare frame index -> path mapping
        var frameIndexPaths = context.Keyframes.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value);

        // Create ephemeral capability atoms for this wave execution
        var backpressure = CapabilityAtoms.CreateBackpressureController(
            minConcurrency: 1,
            maxConcurrency: MaxConcurrency,
            targetLatency: TimeSpan.FromMilliseconds(TargetLatencyMs));
        var estimator = CapabilityAtoms.CreateTimeEstimator();

        // Generate batch embeddings with backpressure control
        var embeddings = await _batchClipService!.GenerateBatchEmbeddingsAsync(
            frameIndexPaths, backpressure, estimator, BatchSize, ct);

        var avgBatchTime = estimator.GetAverageDuration("clip_batch");
        var backpressureStatus = backpressure.GetStatus();

        // Store embeddings in context
        foreach (var (frameIndex, embedding) in embeddings)
        {
            context.KeyframeEmbeddings[frameIndex] = embedding;
        }

        _logger.LogInformation(
            "Batch CLIP: {Count}/{Total} embeddings ({AvgBatch:F1}ms/batch avg, concurrency {Concurrency}, batch size {BatchSize})",
            embeddings.Count, frameIndexPaths.Count, avgBatchTime.TotalMilliseconds,
            backpressureStatus.CurrentConcurrency, BatchSize);

        // Get time estimates for throughput calculation
        var timeEstimate = estimator.GetEstimate("clip_batch", remainingCount: 0);
        var totalBatches = (frameIndexPaths.Count + BatchSize - 1) / BatchSize;
        var totalTimeMs = avgBatchTime.TotalMilliseconds * totalBatches;
        var throughput = totalTimeMs > 0 ? frameIndexPaths.Count / (totalTimeMs / 1000.0) : 0;

        // Emit signals - salient ones for entity persistence, diagnostic for observability
        context.AddSignals([
            // Core result signals (persist with entity)
            new VideoSignal
            {
                Key = VideoSignals.ClipEmbeddingsReady,
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = VideoSignals.ClipEmbeddingsCount,
                Value = embeddings.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },

            // Timing signals (persist with entity for performance tracking)
            new VideoSignal
            {
                Key = "clip.total_time_ms",
                Value = totalTimeMs,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "timing", "persist"]
            },
            new VideoSignal
            {
                Key = "clip.avg_batch_time_ms",
                Value = avgBatchTime.TotalMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "timing", "persist"]
            },
            new VideoSignal
            {
                Key = "clip.throughput_fps",
                Value = throughput,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "timing", "persist"]
            },

            // Diagnostic signals (ephemeral, for debugging)
            new VideoSignal
            {
                Key = "clip.batch_size",
                Value = BatchSize,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "diagnostic"]
            },
            new VideoSignal
            {
                Key = "clip.batch_count",
                Value = totalBatches,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "diagnostic"]
            },
            new VideoSignal
            {
                Key = "clip.backpressure_concurrency",
                Value = backpressureStatus.CurrentConcurrency,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "diagnostic"]
            },
            new VideoSignal
            {
                Key = "clip.backpressure_throttling",
                Value = backpressureStatus.IsThrottling,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "diagnostic"]
            },
            new VideoSignal
            {
                Key = "clip.time_estimate_confidence",
                Value = timeEstimate.Confidence,
                Source = Name,
                Tags = [VideoSignalTags.Visual, "diagnostic"]
            }
        ]);

        // Check for escalation - if we got significantly fewer embeddings than frames
        if (embeddings.Count < frameIndexPaths.Count * 0.5 && frameIndexPaths.Count > 5)
        {
            _logger.LogWarning("Only {Embeddings}/{Total} embeddings generated - may need escalation",
                embeddings.Count, frameIndexPaths.Count);

            context.AddSignal(new VideoSignal
            {
                Key = VideoSignals.EscalationRequired,
                Value = "incomplete_embeddings",
                Source = Name,
                Confidence = 0.8,
                Tags = [VideoSignalTags.Visual]
            });
        }

        context.ReportProgress("CLIP embedding complete", 100);
    }
}

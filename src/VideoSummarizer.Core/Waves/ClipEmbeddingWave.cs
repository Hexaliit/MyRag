using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Generates CLIP embeddings for keyframes using batch GPU processing.
/// All configuration values come from waves.yaml - NO magic numbers.
/// Emits: clip.embeddings_ready
/// </summary>
public class ClipEmbeddingWave : IVideoWave, ISignalAwareVideoWave
{
    private readonly BatchClipEmbeddingService? _batchClipService;
    private readonly VideoWaveManifestLoader _manifestLoader;
    private readonly ILogger<ClipEmbeddingWave> _logger;

    // Configuration from YAML
    private int BatchSize => _manifestLoader.GetConfigValue<int>(Name, "batch_size", 8);

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

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Generate batch embeddings (GPU-optimized)
        var embeddings = await _batchClipService!.GenerateBatchEmbeddingsAsync(
            frameIndexPaths, BatchSize, ct);

        stopwatch.Stop();

        // Store embeddings in context
        foreach (var (frameIndex, embedding) in embeddings)
        {
            context.KeyframeEmbeddings[frameIndex] = embedding;
        }

        var avgTimePerFrame = frameIndexPaths.Count > 0
            ? stopwatch.ElapsedMilliseconds / frameIndexPaths.Count
            : 0;

        _logger.LogInformation(
            "Batch CLIP: {Count}/{Total} embeddings in {Time}ms ({Avg}ms/frame avg, batch size {BatchSize})",
            embeddings.Count, frameIndexPaths.Count, stopwatch.ElapsedMilliseconds,
            avgTimePerFrame, BatchSize);

        // Emit signals
        context.AddSignals([
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
            new VideoSignal
            {
                Key = VideoSignals.ClipBatchSize,
                Value = BatchSize,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "clip.processing_time_ms",
                Value = stopwatch.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "clip.avg_time_per_frame_ms",
                Value = avgTimePerFrame,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
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

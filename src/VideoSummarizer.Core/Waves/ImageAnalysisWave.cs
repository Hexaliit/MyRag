using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using VideoSummarizer.Core.Coordination;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Runs ImageSummarizer analysis on keyframes for OCR, vision, and captions.
///     Chains to ImageSummarizer's wave orchestrator for full image analysis.
///     CLIP embedding is already done by ClipEmbeddingWave, so this focuses on OCR/vision.
///     Emits: keyframes.analyzed, ocr.extracted, captions.generated
/// </summary>
public class ImageAnalysisWave : IVideoWave, ISignalAwareVideoWave
{
    private const int MaxParallelAnalysis = 2; // GPU contention limits
    private readonly WaveOrchestrator? _imageOrchestrator;
    private readonly ILogger<ImageAnalysisWave> _logger;

    public ImageAnalysisWave(
        ILogger<ImageAnalysisWave> logger,
        WaveOrchestrator? imageOrchestrator = null)
    {
        _imageOrchestrator = imageOrchestrator;
        _logger = logger;
    }

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.KeyframesExtracted];
    public IReadOnlyList<string> OptionalSignals => [VideoSignals.ClipEmbeddingsReady];

    public IReadOnlyList<string> EmittedSignals =>
    [
        VideoSignals.KeyframesAnalyzed,
        VideoSignals.OcrExtracted,
        VideoSignals.CaptionsGenerated
    ];

    public IReadOnlyList<string> CacheEmits => [];
    public IReadOnlyList<string> CacheUses => ["extracted_frames"];

    public string Name => "image_analysis";
    public int Priority => 790; // After CLIP embedding
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, VideoSignalTags.Ocr];

    public bool ShouldRun(VideoContext context)
    {
        return _imageOrchestrator != null &&
               context.Keyframes.Count > 0;
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        context.ReportProgress("Analyzing keyframes (OCR, vision)", 0);

        // Emit wave started signal
        context.AddSignal(new VideoSignal
        {
            Key = $"{Name}.started",
            Value = DateTimeOffset.UtcNow,
            Source = Name,
            Tags = [VideoSignalTags.Visual, "wave", "flow"]
        });

        var total = context.Keyframes.Count;
        var processed = 0;
        var ocrCount = 0;
        var captionCount = 0;

        _logger.LogInformation("Analyzing {Count} keyframes with ImageSummarizer (parallelism={Parallelism})",
            total, MaxParallelAnalysis);

        // Process frames in parallel with limit
        var semaphore = new SemaphoreSlim(MaxParallelAnalysis);

        var tasks = context.Keyframes.Select(async kvp =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var (frameIndex, framePath) = kvp;
                var timestamp = context.FrameTimestamps.GetValueOrDefault(frameIndex, 0);

                try
                {
                    // Run ImageSummarizer wave orchestrator on the keyframe
                    var profile = await _imageOrchestrator!.AnalyzeAsync(framePath, ct);

                    // If CLIP wasn't done by ClipEmbeddingWave, get from profile
                    if (!context.KeyframeEmbeddings.ContainsKey(frameIndex))
                    {
                        var embedding = profile.GetValue<float[]>("vision.clip.embedding");
                        if (embedding != null) context.KeyframeEmbeddings[frameIndex] = embedding;
                    }

                    // Extract perceptual hash
                    var phash = profile.GetValue<string>("identity.phash");
                    if (!string.IsNullOrEmpty(phash)) context.PerceptualHashes[frameIndex] = phash;

                    // Add OCR text to context
                    var ocrText = profile.GetValue<string>("ocr.text");
                    if (!string.IsNullOrEmpty(ocrText))
                    {
                        Interlocked.Increment(ref ocrCount);
                        context.SetCached($"ocr.{frameIndex}", new KeyframeOcrResult
                        {
                            FrameIndex = frameIndex,
                            Timestamp = timestamp,
                            Text = ocrText,
                            Confidence = profile.GetValue<double?>("ocr.confidence") ?? 0.5,
                            BoundingBoxes = profile.GetValue<List<object>>("ocr.boxes")
                        });
                    }

                    // Add caption to context
                    var caption = profile.GetValue<string>("vision.caption");
                    if (!string.IsNullOrEmpty(caption))
                    {
                        Interlocked.Increment(ref captionCount);
                        context.SetCached($"caption.{frameIndex}", caption);
                    }

                    // Store full profile for later use
                    context.SetCached($"image_profile.{frameIndex}", profile);

                    _logger.LogDebug(
                        "Analyzed keyframe {FrameIndex} at {Timestamp:F2}s: ocr={HasOcr}, caption={HasCaption}",
                        frameIndex, timestamp, !string.IsNullOrEmpty(ocrText), !string.IsNullOrEmpty(caption));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze keyframe {FrameIndex} at {Timestamp:F2}s",
                        frameIndex, timestamp);
                }

                var current = Interlocked.Increment(ref processed);
                var progress = (double)current / total * 100;
                context.ReportProgress($"Analyzing keyframe {current}/{total}", progress);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "ImageSummarizer analysis complete: {Total} frames, {OCR} with OCR, {Captions} with captions",
            total, ocrCount, captionCount);

        // Emit signals
        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.KeyframesAnalyzed,
                Value = processed,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = VideoSignals.OcrExtracted,
                Value = ocrCount,
                Source = Name,
                Tags = [VideoSignalTags.Ocr]
            },
            new VideoSignal
            {
                Key = VideoSignals.CaptionsGenerated,
                Value = captionCount,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        // Check for escalation - low OCR confidence
        if (ocrCount > 0)
        {
            var avgConfidence = CalculateAverageOcrConfidence(context);
            if (avgConfidence < 0.5)
            {
                _logger.LogInformation("Low OCR confidence ({Confidence:F2}) - escalating to vision LLM",
                    avgConfidence);
                context.AddSignal(new VideoSignal
                {
                    Key = VideoSignals.EscalationVisionLlm,
                    Value = true,
                    Source = Name,
                    Confidence = avgConfidence,
                    Tags = [VideoSignalTags.Ocr]
                });
            }
        }

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

        context.ReportProgress("Image analysis complete", 100);
    }

    private double CalculateAverageOcrConfidence(VideoContext context)
    {
        var confidences = new List<double>();
        foreach (var frameIndex in context.Keyframes.Keys)
        {
            var ocr = context.GetCached<KeyframeOcrResult>($"ocr.{frameIndex}");
            if (ocr != null) confidences.Add(ocr.Confidence);
        }

        return confidences.Count > 0 ? confidences.Average() : 0;
    }
}
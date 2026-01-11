using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Summarizer.Core.Capabilities;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Stage 2: Keyframe extraction using codec I-frames + ImageSummarizer.
/// Leverages FFmpeg to identify natural keyframes from the compressed stream,
/// then chains to ImageSummarizer for CLIP embeddings, OCR, and analysis.
///
/// Optimization: Uses perceptual hash deduplication to skip visually similar
/// frames before expensive ImageSummarizer analysis (20-40% reduction).
/// </summary>
public class KeyframeExtractionWave : IVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly KeyframeDeduplicationService? _deduplicationService;
    private readonly BatchClipEmbeddingService? _batchClipService;
    private readonly WaveOrchestrator? _imageOrchestrator;
    private readonly ILogger<KeyframeExtractionWave> _logger;

    // Configuration
    private const int MaxKeyframesToProcess = 50; // Limit for long videos
    private const double MinKeyframeInterval = 2.0; // Minimum seconds between keyframes
    private const int MaxParallelAnalysis = 2; // Limited parallelism (GPU contention limits this)
    private const int DeduplicationHammingThreshold = 10; // Lower = stricter matching
    private const int ThumbnailWidth = 128; // Low-res for fast deduplication
    private const int ClipBatchSize = 8; // Images per CLIP batch

    public string Name => "keyframe_extraction";
    public int Priority => 800; // After shot detection
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, VideoSignalTags.Shot];

    public KeyframeExtractionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<KeyframeExtractionWave> logger,
        KeyframeDeduplicationService? deduplicationService = null,
        BatchClipEmbeddingService? batchClipService = null,
        WaveOrchestrator? imageOrchestrator = null)
    {
        _ffmpegService = ffmpegService;
        _deduplicationService = deduplicationService;
        _batchClipService = batchClipService;
        _imageOrchestrator = imageOrchestrator;
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        context.Metadata != null && context.Shots.Count > 0;

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Extracting keyframes", 0);

        var videoPath = context.VideoPath;
        var keyframeDir = Path.Combine(context.WorkingDirectory, "keyframes");
        var thumbDir = Path.Combine(context.WorkingDirectory, "keyframes_thumb");
        Directory.CreateDirectory(keyframeDir);
        Directory.CreateDirectory(thumbDir);

        // 1. Get codec-level I-frames (fast - no decoding)
        context.ReportProgress("Finding codec keyframes", 5);
        var iframes = await _ffmpegService.ExtractIFramesAsync(videoPath, ct);

        _logger.LogInformation("Found {Count} codec I-frames", iframes.Count);

        // 2. Select which keyframes to extract based on shots
        var keyframeTimestamps = SelectKeyframeTimestamps(context, iframes);

        _logger.LogInformation("Selected {Count} keyframes for extraction", keyframeTimestamps.Count);

        // 3. OPTIMIZATION: Extract LOW-RES thumbnails first for deduplication
        Dictionary<double, string> framesToAnalyze;
        var duplicatesSkipped = 0;

        if (_deduplicationService != null && keyframeTimestamps.Count > 5)
        {
            context.ReportProgress("Extracting thumbnails for deduplication", 10);

            // Extract small thumbnails (128px wide) - much faster than full-res
            var thumbnails = await _ffmpegService.ExtractFramesAtTimestampsAsync(
                videoPath,
                keyframeTimestamps.Select(k => k.Timestamp),
                thumbDir,
                ct,
                prefix: "thumb_",
                width: ThumbnailWidth);

            _logger.LogInformation("Extracted {Count} thumbnails for deduplication", thumbnails.Count);

            // Deduplicate based on thumbnails
            context.ReportProgress("Deduplicating similar frames", 15);
            var uniqueFrames = await _deduplicationService.FilterSimilarFramesAsync(
                thumbnails, DeduplicationHammingThreshold, ct);

            var uniqueTimestamps = uniqueFrames.Select(f => f.Timestamp).ToHashSet();
            duplicatesSkipped = keyframeTimestamps.Count - uniqueTimestamps.Count;

            _logger.LogInformation(
                "Deduplication: {Original} -> {Unique} frames ({Skipped} similar frames skipped)",
                keyframeTimestamps.Count, uniqueTimestamps.Count, duplicatesSkipped);

            // Store dHash values for later use
            foreach (var frame in uniqueFrames)
            {
                var frameIndex = (int)(frame.Timestamp * context.Metadata!.Fps);
                context.SetCached($"keyframe_dhash.{frameIndex}", frame.DHash);
            }

            // 4. Extract FULL-RES frames only for unique timestamps
            context.ReportProgress("Extracting unique frames (full resolution)", 20);
            var extractedFrames = await _ffmpegService.ExtractFramesAtTimestampsAsync(
                videoPath,
                uniqueTimestamps,
                keyframeDir,
                ct);

            _logger.LogInformation("Extracted {Count} full-res keyframes (skipped {Skipped} duplicates)",
                extractedFrames.Count, duplicatesSkipped);

            // Store frame paths in context
            foreach (var (timestamp, path) in extractedFrames)
            {
                var frameIndex = (int)(timestamp * context.Metadata!.Fps);
                context.Keyframes[frameIndex] = path;
                context.FrameTimestamps[frameIndex] = timestamp;
            }

            framesToAnalyze = extractedFrames;
        }
        else
        {
            // No deduplication - extract all frames directly
            context.ReportProgress("Extracting frame images", 15);
            var extractedFrames = await _ffmpegService.ExtractFramesAtTimestampsAsync(
                videoPath,
                keyframeTimestamps.Select(k => k.Timestamp),
                keyframeDir,
                ct);

            _logger.LogInformation("Extracted {Count} frame images", extractedFrames.Count);

            // Store frame paths in context
            foreach (var (timestamp, path) in extractedFrames)
            {
                var frameIndex = (int)(timestamp * context.Metadata!.Fps);
                context.Keyframes[frameIndex] = path;
                context.FrameTimestamps[frameIndex] = timestamp;
            }

            framesToAnalyze = extractedFrames;
        }

        // 5. Run ImageSummarizer analysis on unique keyframes (if available)
        if (_imageOrchestrator != null)
        {
            context.ReportProgress("Analyzing keyframes with ImageSummarizer", 30);
            await AnalyzeKeyframesAsync(context, framesToAnalyze, ct);
        }
        else
        {
            _logger.LogWarning("ImageSummarizer not available - skipping frame analysis");
        }

        // 6. Update shots with keyframe info
        context.ReportProgress("Updating shot keyframes", 90);
        UpdateShotKeyframes(context);

        // Add summary signals
        context.AddSignals([
            new VideoSignal
            {
                Key = "keyframes.count",
                Value = framesToAnalyze.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "keyframes.candidates",
                Value = keyframeTimestamps.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "keyframes.duplicates_skipped",
                Value = duplicatesSkipped,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "keyframes.codec_iframes",
                Value = iframes.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "keyframes.with_embeddings",
                Value = context.KeyframeEmbeddings.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "keyframes.dedup_enabled",
                Value = _deduplicationService != null,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        context.ReportProgress("Keyframe extraction complete", 100);
    }

    /// <summary>
    /// Select which keyframes to extract based on shots and I-frame positions.
    /// Prioritizes: shot keyframes, codec I-frames aligned with shots, regular intervals.
    /// </summary>
    private List<KeyframeSelection> SelectKeyframeTimestamps(VideoContext context, List<IFrameInfo> iframes)
    {
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
                    ShotId = shot.Id
                });
                usedTimestamps.Add(timestamp);
            }
        }

        // Second pass: add I-frames that aren't near existing keyframes
        foreach (var iframe in iframes)
        {
            if (selections.Count >= MaxKeyframesToProcess) break;

            // Check if too close to an existing keyframe
            var tooClose = usedTimestamps.Any(t => Math.Abs(t - iframe.Timestamp) < MinKeyframeInterval);
            if (tooClose) continue;

            selections.Add(new KeyframeSelection
            {
                Timestamp = iframe.Timestamp,
                Source = "codec_iframe"
            });
            usedTimestamps.Add(iframe.Timestamp);
        }

        // Sort by timestamp
        return selections.OrderBy(s => s.Timestamp).Take(MaxKeyframesToProcess).ToList();
    }

    /// <summary>
    /// Run analysis on extracted keyframes.
    /// OPTIMIZATION: First batch CLIP for all embeddings (single GPU pass),
    /// then ImageSummarizer for OCR/vision (with CLIP disabled).
    /// </summary>
    private async Task AnalyzeKeyframesAsync(
        VideoContext context,
        Dictionary<double, string> extractedFrames,
        CancellationToken ct)
    {
        var total = extractedFrames.Count;

        // PHASE 1: Batch CLIP embedding (3-5x faster than per-frame)
        if (_batchClipService != null)
        {
            context.ReportProgress("Generating CLIP embeddings (batch)", 30);

            // Convert timestamp->path to frameIndex->path for batch service
            var frameIndexPaths = extractedFrames.ToDictionary(
                kvp => (int)(kvp.Key * context.Metadata!.Fps),
                kvp => kvp.Value);

            // Create ephemeral capability atoms for this batch operation
            var backpressure = CapabilityAtoms.CreateBackpressureController(
                minConcurrency: 1,
                maxConcurrency: MaxParallelAnalysis,
                targetLatency: TimeSpan.FromMilliseconds(500));
            var estimator = CapabilityAtoms.CreateTimeEstimator();

            var embeddings = await _batchClipService.GenerateBatchEmbeddingsAsync(
                frameIndexPaths, backpressure, estimator, ClipBatchSize, ct);

            // Store embeddings in context
            foreach (var (frameIndex, embedding) in embeddings)
            {
                context.KeyframeEmbeddings[frameIndex] = embedding;
            }

            var avgBatchTime = estimator.GetAverageDuration("clip_batch");
            _logger.LogInformation(
                "Batch CLIP: {Count}/{Total} embeddings ({AvgBatch:F1}ms/batch avg)",
                embeddings.Count, total, avgBatchTime.TotalMilliseconds);
        }

        // PHASE 2: ImageSummarizer for OCR, vision LLM, etc. (CLIP disabled/already done)
        if (_imageOrchestrator != null)
        {
            context.ReportProgress("Analyzing keyframes (OCR, vision)", 50);
            await AnalyzeKeyframesWithImageSummarizerAsync(context, extractedFrames, ct);
        }

        _logger.LogInformation("Analyzed {Count} keyframes, {Embeddings} with embeddings",
            total, context.KeyframeEmbeddings.Count);
    }

    /// <summary>
    /// Run ImageSummarizer on keyframes for OCR, vision LLM, etc.
    /// CLIP embedding is already done via batch, so this focuses on other waves.
    /// </summary>
    private async Task AnalyzeKeyframesWithImageSummarizerAsync(
        VideoContext context,
        Dictionary<double, string> extractedFrames,
        CancellationToken ct)
    {
        var total = extractedFrames.Count;
        var processed = 0;

        _logger.LogInformation("Processing {Count} keyframes for OCR/vision with parallelism={Parallelism}",
            total, MaxParallelAnalysis);

        // Process in parallel with limit
        var semaphore = new SemaphoreSlim(MaxParallelAnalysis);

        var tasks = extractedFrames.Select(async kvp =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var (timestamp, framePath) = kvp;
                var frameIndex = (int)(timestamp * context.Metadata!.Fps);

                try
                {
                    // Run ImageSummarizer wave orchestrator on the keyframe
                    // Note: CLIP wave may run but embeddings are already stored
                    var profile = await _imageOrchestrator!.AnalyzeAsync(framePath, ct);

                    // If batch CLIP wasn't available, try to get embedding from profile
                    if (!context.KeyframeEmbeddings.ContainsKey(frameIndex))
                    {
                        var embedding = profile.GetValue<float[]>("vision.clip.embedding");
                        if (embedding != null)
                        {
                            context.KeyframeEmbeddings[frameIndex] = embedding;
                        }
                    }

                    // Extract perceptual hash if available
                    var phash = profile.GetValue<string>("identity.phash");
                    if (!string.IsNullOrEmpty(phash))
                    {
                        context.PerceptualHashes[frameIndex] = phash;
                    }

                    // Add OCR text to context (for TextTrack building)
                    var ocrText = profile.GetValue<string>("ocr.text");
                    if (!string.IsNullOrEmpty(ocrText))
                    {
                        context.SetCached($"ocr.{frameIndex}", new KeyframeOcrResult
                        {
                            FrameIndex = frameIndex,
                            Timestamp = timestamp,
                            Text = ocrText,
                            Confidence = profile.GetValue<double?>("ocr.confidence") ?? 0.5,
                            BoundingBoxes = profile.GetValue<List<object>>("ocr.boxes")
                        });
                    }

                    // Add caption to context (for evidence)
                    var caption = profile.GetValue<string>("vision.caption");
                    if (!string.IsNullOrEmpty(caption))
                    {
                        context.SetCached($"caption.{frameIndex}", caption);
                    }

                    // Store full profile for later use
                    context.SetCached($"image_profile.{frameIndex}", profile);

                    _logger.LogDebug("Analyzed keyframe at {Timestamp:F2}s: ocr={HasOcr}",
                        timestamp, !string.IsNullOrEmpty(ocrText));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze keyframe at {Timestamp:F2}s", timestamp);
                }

                Interlocked.Increment(ref processed);
                var progress = 50 + (40.0 * processed / total);
                context.ReportProgress($"Analyzing keyframe {processed}/{total}", progress);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Update shot segments with keyframe paths and embeddings.
    /// </summary>
    private void UpdateShotKeyframes(VideoContext context)
    {
        foreach (var shot in context.Shots)
        {
            // Find best keyframe for this shot
            var keyframeIndex = shot.KeyframeIndex;

            // Check if we have a keyframe path
            if (context.Keyframes.TryGetValue(keyframeIndex, out var keyframePath))
            {
                // Note: ShotSegment is a record, so we'd need to create a new one
                // For now, store mapping in context cache
                context.SetCached($"shot_keyframe.{shot.Id}", keyframePath);
            }

            // Get embedding for this keyframe
            if (context.KeyframeEmbeddings.TryGetValue(keyframeIndex, out var embedding))
            {
                context.SetCached($"shot_embedding.{shot.Id}", embedding);
            }
        }
    }

    private record KeyframeSelection
    {
        public double Timestamp { get; init; }
        public string Source { get; init; } = "";
        public Guid? ShotId { get; init; }
    }
}

/// <summary>
/// OCR result for a keyframe.
/// </summary>
public record KeyframeOcrResult
{
    public int FrameIndex { get; init; }
    public double Timestamp { get; init; }
    public string Text { get; init; } = "";
    public double Confidence { get; init; }
    public List<object>? BoundingBoxes { get; init; }
}

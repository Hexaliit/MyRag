using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using OpenCvSharp;
using static Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Florence-2 Wave - Fast local captioning and OCR using ONNX models.
/// Provides sub-second inference without requiring external services.
/// Uses ColorWave signals to compensate for Florence-2's weak color detection.
/// Also runs OpenCV complexity assessment to help decide on LLM escalation.
/// Priority: 56 (before MotionWave so its entities can be reused for motion identification)
///
/// In full learning mode, Florence-2 runs alongside Vision LLM to compare results
/// and learn from differences between fast/local vs slow/cloud approaches.
/// </summary>
public class Florence2Wave : IAnalysisWave
{
    private readonly Florence2CaptionService _florence2Service;
    private readonly IOptions<ImageConfig> _configOptions;
    private readonly ILogger<Florence2Wave>? _logger;

    private ImageConfig Config => _configOptions.Value;

    public string Name => "Florence2Wave";
    public int Priority => 56; // Before MotionWave (55) so entities are available for motion ID
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "vision", "florence2", "onnx", "local" };

    public Florence2Wave(
        Florence2CaptionService florence2Service,
        IOptions<ImageConfig> config,
        ILogger<Florence2Wave>? logger = null)
    {
        _florence2Service = florence2Service;
        _configOptions = config;
        _logger = logger;
    }

    /// <summary>
    /// Florence-2 should run if it's enabled and available.
    /// For animated GIFs: Skip if MlOcrWave is using filmstrip mode (VisionLlmWave will handle OCR).
    /// It's a fast alternative to Vision LLM that works offline.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Check if Florence-2 is enabled
        if (!Config.EnableFlorence2)
            return false;

        // OPTIMIZATION: For animated GIFs in filmstrip mode, skip Florence-2 per-frame OCR
        // MlOcrWave has cached frames and VisionLlmWave will create a text-only strip
        // This saves ~15-20 seconds of per-frame Florence-2 processing
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        var frameCount = context.GetValue<int>("identity.frame_count");
        var deferToVisionLlm = context.GetValue<bool>("ocr.ml.defer_to_visionllm");

        if (isAnimated && frameCount > 1 && deferToVisionLlm)
        {
            _logger?.LogDebug("Skipping Florence2Wave: filmstrip mode active (MlOcrWave deferred to VisionLLM)");
            return false;
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Use preprocessed image if available (from OcrPreprocessingWave)
        var effectivePath = context.GetCached<string>("preprocessing.enhanced_image_path") ?? imagePath;

        // Check if Florence-2 is available
        if (!await _florence2Service.IsAvailableAsync(ct))
        {
            _logger?.LogDebug("Florence-2 models not available, skipping");
            signals.Add(new Signal
            {
                Key = "florence2.available",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "florence2", "status" }
            });
            return signals;
        }

        signals.Add(new Signal
        {
            Key = "florence2.available",
            Value = true,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "florence2", "status" }
        });

        try
        {
            // Get caption with OCR using Florence-2
            var result = await _florence2Service.GetCaptionAsync(
                effectivePath,
                detailed: true,
                enhanceWithColors: true, // Use ColorWave signals
                ct: ct);

            if (!result.Success)
            {
                _logger?.LogWarning("Florence-2 failed: {Error}", result.Error);
                signals.Add(new Signal
                {
                    Key = "florence2.error",
                    Value = result.Error,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "florence2", "error" }
                });
                return signals;
            }

            // Add visual description signal (what Florence-2 calls "caption")
            if (!string.IsNullOrWhiteSpace(result.Caption))
            {
                var confidence = CalculateCaptionConfidence(result);

                // Emit florence2-specific signal
                signals.Add(new Signal
                {
                    Key = "florence2.description",
                    Value = result.Caption,
                    Confidence = confidence,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "description", "florence2", "onnx" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = "florence-2-base",
                        ["duration_ms"] = result.DurationMs,
                        ["enhanced_with_colors"] = result.EnhancedWithColors,
                        ["frame_count"] = result.FrameCount
                    }
                });

                // ALSO emit standard vision.description signal for ImagePipeline consumption
                signals.Add(new Signal
                {
                    Key = "vision.description",
                    Value = result.Caption,
                    Confidence = confidence,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "description", "vision" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["source_wave"] = "florence2",
                        ["model"] = "florence-2-base"
                    }
                });

                // For backward compatibility, also emit as vision.caption
                signals.Add(new Signal
                {
                    Key = "vision.caption",
                    Value = result.Caption,
                    Confidence = confidence,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "caption", "vision" }
                });
            }

            // Add OCR text signal if detected
            if (!string.IsNullOrWhiteSpace(result.OcrText))
            {
                signals.Add(new Signal
                {
                    Key = "florence2.ocr_text",
                    Value = result.OcrText,
                    Confidence = 0.75, // Florence-2 OCR is good but not as accurate as Tesseract
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "ocr", "text", "florence2" }
                });

                // Also emit as content.extracted_text for compatibility
                // but only if we don't already have OCR text from a better source
                var existingOcr = context.GetValue<string>("content.extracted_text");
                if (string.IsNullOrWhiteSpace(existingOcr))
                {
                    signals.Add(new Signal
                    {
                        Key = "content.extracted_text",
                        Value = result.OcrText,
                        Confidence = 0.7, // Lower than Tesseract
                        Source = Name,
                        Tags = new List<string> { SignalTags.Content, "text" }
                    });
                }
            }

            // Add timing signal
            signals.Add(new Signal
            {
                Key = "florence2.duration_ms",
                Value = result.DurationMs,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "florence2", "performance" }
            });

            // Add scene detection signals for animated GIFs (useful for other waves)
            if (result.SceneDetection != null)
            {
                signals.Add(new Signal
                {
                    Key = "scene.count",
                    Value = result.SceneDetection.SceneCount,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "motion", "animation" }
                });

                signals.Add(new Signal
                {
                    Key = "scene.frame_indices",
                    Value = result.SceneDetection.SceneEndFrameIndices,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "frames" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["total_frames"] = result.SceneDetection.TotalFrames,
                        ["used_motion_detection"] = result.SceneDetection.UsedMotionDetection
                    }
                });

                signals.Add(new Signal
                {
                    Key = "scene.last_frame",
                    Value = result.SceneDetection.LastSceneFrameIndex,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "frames" }
                });

                signals.Add(new Signal
                {
                    Key = "scene.avg_motion",
                    Value = result.SceneDetection.AverageMotion,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "motion" }
                });

                _logger?.LogDebug(
                    "Scene detection: {SceneCount} scenes from {TotalFrames} frames (avgMotion={AvgMotion:F3})",
                    result.SceneDetection.SceneCount,
                    result.SceneDetection.TotalFrames,
                    result.SceneDetection.AverageMotion);
            }

            // Run NER-focused entity extraction
            var nerResult = await _florence2Service.ExtractEntitiesAsync(effectivePath, ct);
            if (nerResult.Success && nerResult.Entities.Count > 0)
            {
                // Emit individual entity signals
                foreach (var entity in nerResult.Entities)
                {
                    signals.Add(new Signal
                    {
                        Key = $"florence2.entity.{entity.Type.ToLowerInvariant()}",
                        Value = entity.Name,
                        Confidence = entity.Confidence,
                        Source = Name,
                        Tags = new List<string> { SignalTags.Content, "entity", "ner", entity.Type.ToLowerInvariant() }
                    });
                }

                // Emit aggregated entity types signal
                signals.Add(new Signal
                {
                    Key = "florence2.entity_types",
                    Value = nerResult.Entities.Select(e => e.Type).Distinct().ToArray(),
                    Confidence = nerResult.Entities.Average(e => e.Confidence),
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "entities", "ner" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["entity_count"] = nerResult.Entities.Count,
                        ["entities"] = nerResult.Entities.Select(e => new { e.Name, e.Type, e.Confidence }).ToList()
                    }
                });

                // Emit short description for NER if different from main caption
                if (!string.IsNullOrWhiteSpace(nerResult.ShortDescription))
                {
                    signals.Add(new Signal
                    {
                        Key = "florence2.ner_description",
                        Value = nerResult.ShortDescription,
                        Confidence = 0.8,
                        Source = Name,
                        Tags = new List<string> { SignalTags.Content, "description", "ner" }
                    });
                }

                _logger?.LogDebug("Florence-2 NER: extracted {Count} entities", nerResult.Entities.Count);
            }

            // Determine if we should escalate to Vision LLM
            var shouldEscalate = ShouldEscalateToLlm(result, context);
            signals.Add(new Signal
            {
                Key = "florence2.should_escalate",
                Value = shouldEscalate,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "florence2", "escalation" }
            });

            _logger?.LogDebug(
                "Florence-2 completed in {DurationMs}ms: Caption={HasCaption}, OCR={HasOcr}, Entities={EntityCount}, ShouldEscalate={ShouldEscalate}",
                result.DurationMs,
                !string.IsNullOrWhiteSpace(result.Caption),
                !string.IsNullOrWhiteSpace(result.OcrText),
                nerResult.Entities.Count,
                shouldEscalate);

            return signals;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Florence-2 wave failed for {Path}", imagePath);
            signals.Add(new Signal
            {
                Key = "florence2.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "florence2", "error" }
            });
            return signals;
        }
    }

    /// <summary>
    /// Calculate confidence score for Florence-2 caption based on various factors.
    /// </summary>
    private double CalculateCaptionConfidence(Florence2CaptionResult result)
    {
        var confidence = 0.8; // Base confidence for Florence-2

        // Boost for color enhancement (means we added accurate color info)
        if (result.EnhancedWithColors)
        {
            confidence += 0.05;
        }

        // Slight penalty for multi-frame GIFs (caption may be less focused)
        if (result.FrameCount > 4)
        {
            confidence -= 0.05;
        }

        // Boost if we also got OCR text (supports the caption)
        if (!string.IsNullOrWhiteSpace(result.OcrText))
        {
            confidence += 0.03;
        }

        return Math.Min(0.95, Math.Max(0.6, confidence));
    }

    /// <summary>
    /// Determine if we should escalate to a more powerful Vision LLM.
    /// Uses OpenCV complexity assessment and other signals.
    /// Florence-2 is weak at describing animations, so always escalate GIFs.
    /// </summary>
    private bool ShouldEscalateToLlm(Florence2CaptionResult result, AnalysisContext context)
    {
        // Escalate if no caption was generated
        if (string.IsNullOrWhiteSpace(result.Caption))
        {
            _logger?.LogDebug("Florence-2 escalating: no caption generated");
            return true;
        }

        // Escalate if caption is very short (may be incomplete)
        if (result.Caption.Length < 20)
        {
            _logger?.LogDebug("Florence-2 escalating: caption too short ({Length} chars)", result.Caption.Length);
            return true;
        }

        // Always escalate for animated GIFs - Florence-2 produces generic "animated image" descriptions
        if (result.FrameCount > 1)
        {
            _logger?.LogDebug("Florence-2 escalating: animated GIF ({FrameCount} frames)", result.FrameCount);
            return true;
        }

        // Escalate if caption contains generic animation descriptions (Florence-2 limitation)
        var captionLower = result.Caption.ToLowerInvariant();
        if (captionLower.Contains("animated image") ||
            captionLower.Contains("general motion") ||
            captionLower.Contains("moving image"))
        {
            _logger?.LogDebug("Florence-2 escalating: generic animation description detected");
            return true;
        }

        // Escalate if image has high text likeliness but Florence-2 found no OCR
        var textLikeliness = context.GetValue<double>("content.text_likeliness");
        if (textLikeliness > 0.6 && string.IsNullOrWhiteSpace(result.OcrText))
        {
            _logger?.LogDebug("Florence-2 escalating: high text likeliness but no OCR");
            return true;
        }

        // Escalate if image has motion (Florence-2 may miss animation details)
        var hasMotion = context.GetValue<bool>("motion.has_motion");
        var motionType = context.GetValue<string>("motion.type");
        if (hasMotion && motionType != "static")
        {
            _logger?.LogDebug("Florence-2 escalating: detected motion ({Type})", motionType);
            return true;
        }

        // Escalate if image type suggests complexity
        var imageType = context.GetValue<string>("content.type");
        if (imageType is "Diagram" or "Chart" or "ScannedDocument")
        {
            _logger?.LogDebug("Florence-2 escalating: complex image type ({Type})", imageType);
            return true;
        }

        // Check OpenCV complexity (edge density from ColorWave)
        var edgeDensity = context.GetValue<double>("quality.edge_density");
        if (edgeDensity > Config.Florence2ComplexityThreshold)
        {
            _logger?.LogDebug("Florence-2 escalating: high complexity (edge density {Density})", edgeDensity);
            return true;
        }

        // Default: don't escalate, Florence-2 is probably sufficient
        return false;
    }

    /// <summary>
    /// Quick OpenCV complexity assessment using Canny edge detection.
    /// Returns normalized edge density (0-1).
    /// </summary>
    private (double edgeDensity, double laplacianVariance) AssessComplexityOpenCv(string imagePath)
    {
        try
        {
            using var img = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (img.Empty())
            {
                return (0, 0);
            }

            // Resize for consistent analysis
            var maxDim = 512;
            if (img.Width > maxDim || img.Height > maxDim)
            {
                var scale = Math.Min((double)maxDim / img.Width, (double)maxDim / img.Height);
                Cv2.Resize(img, img, new Size((int)(img.Width * scale), (int)(img.Height * scale)));
            }

            // Edge detection using Canny
            using var edges = new Mat();
            Cv2.Canny(img, edges, 50, 150);
            var edgePixels = Cv2.CountNonZero(edges);
            var totalPixels = edges.Rows * edges.Cols;
            var edgeDensity = (double)edgePixels / totalPixels;

            // Laplacian variance for blur/detail detection
            using var laplacian = new Mat();
            Cv2.Laplacian(img, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stdDev);
            var laplacianVariance = stdDev.Val0 * stdDev.Val0;

            return (edgeDensity, laplacianVariance);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "OpenCV complexity assessment failed");
            return (0, 0);
        }
    }
}

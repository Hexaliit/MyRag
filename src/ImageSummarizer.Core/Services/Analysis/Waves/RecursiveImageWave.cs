using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Recursive Image Wave - Processes extracted picture/figure regions through ImageSummarizer.
/// Runs a subset of analysis waves on each extracted region to generate captions and embeddings.
///
/// Priority: 60 (after TableProfilingWave at 61)
///
/// IMPORTANT: To prevent infinite recursion, this wave:
/// - Sets a "recursion_depth" flag in context
/// - Skips LayoutDetectionWave and LayoutRoutingWave on recursive calls
/// - Limits recursion depth to 1 level
/// </summary>
public class RecursiveImageWave : IAnalysisWave
{
    private readonly IEnumerable<IAnalysisWave> _allWaves;
    private readonly ILogger<RecursiveImageWave>? _logger;

    // Waves to skip during recursive processing (to prevent infinite loops)
    private static readonly HashSet<string> SkippedWaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "LayoutDetectionWave",
        "LayoutRoutingWave",
        "TableExtractionWave",
        "TableProfilingWave",
        "RecursiveImageWave",  // Prevent self-recursion
        "ContradictionWave",   // Run only on parent
        "OcrBenchmarkWave"     // Run only on parent
    };

    // Waves that should run on extracted images
    private static readonly HashSet<string> AllowedWaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "IdentityWave",
        "ColorWave",
        "Florence2Wave",
        "VisionLlmWave",
        "ClipEmbeddingWave",
        "OcrWave",
        "ExifForensicsWave"
    };

    public string Name => "RecursiveImageWave";
    public int Priority => 60; // After TableProfilingWave (61)
    public IReadOnlyList<string> Tags => new[] { "image", "recursive", "extraction", SignalTags.Content };

    public RecursiveImageWave(
        IEnumerable<IAnalysisWave>? allWaves = null,
        ILogger<RecursiveImageWave>? logger = null)
    {
        _allWaves = allWaves ?? Enumerable.Empty<IAnalysisWave>();
        _logger = logger;
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Check for recursion depth to prevent infinite loops
        var depth = context.GetCached<int?>("recursion_depth") ?? 0;
        if (depth >= 1)
        {
            _logger?.LogDebug("Skipping recursive image processing at depth {Depth}", depth);
            return false;
        }

        // Only run if we have extracted picture images
        return context.HasSignal("layout.pictures.extracted");
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var pictureImagePaths = context.GetCached<List<string>>("layout.picture_image_paths");
        if (pictureImagePaths == null || pictureImagePaths.Count == 0)
        {
            _logger?.LogDebug("No picture image paths found in context");
            return signals;
        }

        _logger?.LogInformation("Processing {Count} extracted picture regions from {Image}",
            pictureImagePaths.Count, Path.GetFileName(imagePath));

        var processedImages = new List<ProcessedSubImage>();

        for (int i = 0; i < pictureImagePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var pictureImagePath = pictureImagePaths[i];
            if (!File.Exists(pictureImagePath))
            {
                _logger?.LogWarning("Picture image not found: {Path}", pictureImagePath);
                continue;
            }

            try
            {
                var result = await ProcessSubImageAsync(pictureImagePath, i, ct);
                if (result != null)
                {
                    processedImages.Add(result);
                    EmitSubImageSignals(signals, result, i);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to process sub-image {Index}: {Path}", i, pictureImagePath);
                signals.Add(new Signal
                {
                    Key = $"subimage.{i}.error",
                    Value = ex.Message,
                    Confidence = 0,
                    Source = Name,
                    Tags = new List<string> { "subimage", "error" }
                });
            }
        }

        sw.Stop();

        // Emit processing summary
        if (processedImages.Count > 0)
        {
            signals.Add(new Signal
            {
                Key = "subimage.processing.summary",
                Value = new
                {
                    ImagesProcessed = processedImages.Count,
                    TotalSignals = processedImages.Sum(r => r.SignalCount),
                    WithCaptions = processedImages.Count(r => !string.IsNullOrEmpty(r.Caption)),
                    WithEmbeddings = processedImages.Count(r => r.HasEmbedding),
                    DurationMs = sw.ElapsedMilliseconds
                },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "subimage", "summary" }
            });

            // Cache sub-image results for downstream use
            context.SetCached("subimage.results", processedImages);
        }

        signals.Add(new Signal
        {
            Key = "subimage.processing.duration_ms",
            Value = sw.ElapsedMilliseconds,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "subimage", "timing" }
        });

        _logger?.LogInformation(
            "Recursive image processing complete: {Count} images in {Ms}ms",
            processedImages.Count, sw.ElapsedMilliseconds);

        return signals;
    }

    /// <summary>
    /// Process a single extracted sub-image using a subset of analysis waves.
    /// </summary>
    private async Task<ProcessedSubImage?> ProcessSubImageAsync(
        string imagePath,
        int index,
        CancellationToken ct)
    {
        if (!_allWaves.Any())
        {
            _logger?.LogDebug("No analysis waves available, using fallback analysis");
            return await FallbackAnalysisAsync(imagePath, index, ct);
        }

        // Create a new context for the sub-image with recursion depth set
        var subContext = new AnalysisContext();
        subContext.SetCached("recursion_depth", 1);
        subContext.SetCached("skip_layout_detection", true);

        // Run waves in priority order, filtering for allowed waves
        var allSignals = new List<Signal>();

        foreach (var wave in _allWaves
            .Where(w => !SkippedWaves.Contains(w.Name))
            .Where(w => AllowedWaves.Contains(w.Name))
            .OrderByDescending(w => w.Priority))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (wave.ShouldRun(imagePath, subContext))
                {
                    var waveSignals = await wave.AnalyzeAsync(imagePath, subContext, ct);
                    foreach (var signal in waveSignals)
                    {
                        allSignals.Add(signal);
                        subContext.AddSignal(signal);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Wave {Wave} failed for sub-image {Index}", wave.Name, index);
            }
        }

        // Extract key results
        var caption = subContext.GetValue<string>("vision.llm.caption")
            ?? subContext.GetValue<string>("florence2.caption");
        var embedding = subContext.GetValue<float[]>("clip.embedding");
        var ocrText = subContext.GetValue<string>("ocr.full_text");

        return new ProcessedSubImage
        {
            ImagePath = imagePath,
            Index = index,
            Caption = caption,
            OcrText = ocrText,
            HasEmbedding = embedding != null,
            Embedding = embedding,
            SignalCount = allSignals.Count,
            Signals = allSignals
        };
    }

    /// <summary>
    /// Fallback analysis when orchestrator is not available.
    /// Uses basic file properties only.
    /// </summary>
    private Task<ProcessedSubImage> FallbackAnalysisAsync(
        string imagePath,
        int index,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(imagePath);

        return Task.FromResult(new ProcessedSubImage
        {
            ImagePath = imagePath,
            Index = index,
            Caption = null,
            OcrText = null,
            HasEmbedding = false,
            Embedding = null,
            SignalCount = 0,
            Signals = new List<Signal>(),
            Metadata = new Dictionary<string, object>
            {
                ["file_size"] = fileInfo.Length,
                ["fallback_mode"] = true
            }
        });
    }

    /// <summary>
    /// Emit signals for a processed sub-image.
    /// </summary>
    private void EmitSubImageSignals(List<Signal> signals, ProcessedSubImage result, int index)
    {
        // Caption
        if (!string.IsNullOrEmpty(result.Caption))
        {
            signals.Add(new Signal
            {
                Key = $"subimage.{index}.caption",
                Value = result.Caption,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "subimage", "caption", SignalTags.Content }
            });
        }

        // OCR text
        if (!string.IsNullOrEmpty(result.OcrText))
        {
            signals.Add(new Signal
            {
                Key = $"subimage.{index}.ocr_text",
                Value = result.OcrText,
                Confidence = 0.8,
                Source = Name,
                Tags = new List<string> { "subimage", "ocr", SignalTags.Content }
            });
        }

        // Embedding reference
        if (result.HasEmbedding)
        {
            signals.Add(new Signal
            {
                Key = $"subimage.{index}.has_embedding",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "subimage", "embedding" }
            });

            // Store embedding in metadata for retrieval
            if (result.Embedding != null)
            {
                signals.Add(new Signal
                {
                    Key = $"subimage.{index}.embedding",
                    Value = result.Embedding,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "subimage", "embedding", "vector" }
                });
            }
        }

        // Path reference
        signals.Add(new Signal
        {
            Key = $"subimage.{index}.path",
            Value = result.ImagePath,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "subimage", "path" }
        });

        // Signal count (useful for debugging)
        signals.Add(new Signal
        {
            Key = $"subimage.{index}.signal_count",
            Value = result.SignalCount,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "subimage", "stats" }
        });

        _logger?.LogDebug(
            "Sub-image {Index}: {Signals} signals, caption={HasCaption}, embedding={HasEmbed}",
            index, result.SignalCount,
            !string.IsNullOrEmpty(result.Caption),
            result.HasEmbedding);
    }
}

/// <summary>
/// Result of processing an extracted sub-image.
/// </summary>
public record ProcessedSubImage
{
    public required string ImagePath { get; init; }
    public int Index { get; init; }
    public string? Caption { get; init; }
    public string? OcrText { get; init; }
    public bool HasEmbedding { get; init; }
    public float[]? Embedding { get; init; }
    public int SignalCount { get; init; }
    public List<Signal> Signals { get; init; } = new();
    public Dictionary<string, object>? Metadata { get; init; }
}

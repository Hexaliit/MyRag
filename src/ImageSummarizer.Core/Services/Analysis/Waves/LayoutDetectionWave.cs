using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
///     Layout Detection Wave - Detects document layout elements using YOLOv10-DocLayNet.
///     Identifies: Text, Title, Table, Figure, List, Caption, Footnote, Header, Footer.
///     Auto-downloads YOLOv10-DocLayNet ONNX model on first use (~62MB).
///     Priority: 64 (runs early, before OCR to guide text extraction).
/// </summary>
public class LayoutDetectionWave : IAnalysisWave
{
    // YOLOv10-DocLayNet model constants
    private const int ModelInputSize = 640; // Standard YOLO input size
    private const float ConfidenceThreshold = 0.3f;
    private const float NmsThreshold = 0.5f;
    private static InferenceSession? _layoutSession;
    private static readonly object _modelLock = new();

    // DocLayNet class names (11 classes)
    private static readonly string[] ClassNames =
    {
        "Caption",
        "Footnote",
        "Formula",
        "List-item",
        "Page-footer",
        "Page-header",
        "Picture",
        "Section-header",
        "Table",
        "Text",
        "Title"
    };

    private readonly ImageConfig _config;
    private readonly ILogger<LayoutDetectionWave>? _logger;
    private readonly ModelDownloader? _modelDownloader;
    private readonly OnnxSessionFactory? _sessionFactory;

    public LayoutDetectionWave(
        IOptions<ImageConfig> config,
        ModelDownloader? modelDownloader = null,
        OnnxSessionFactory? sessionFactory = null,
        ILogger<LayoutDetectionWave>? logger = null)
    {
        _config = config.Value;
        _modelDownloader = modelDownloader;
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    public string Name => "LayoutDetectionWave";
    public int Priority => 64; // Before OCR waves (60) to guide text extraction
    public IReadOnlyList<string> Tags => new[] { "layout", "detection", "ml", SignalTags.Content };

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if auto-routing says to skip this wave
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Skip if layout detection is disabled
        if (!_config.EnableLayoutDetection)
            return false;

        // Skip for animated images (layout detection is for documents)
        if (context.GetValue<bool>("identity.is_animated"))
            return false;

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        // Skip if disabled
        if (!_config.EnableLayoutDetection)
        {
            signals.Add(new Signal
            {
                Key = "layout.detection.disabled",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "layout", "config" }
            });
            return signals;
        }

        try
        {
            // Load model (lazy, thread-safe)
            var session = await GetOrLoadModelAsync(ct);
            if (session == null)
            {
                _logger?.LogWarning("Layout detection model not available, skipping");
                signals.Add(new Signal
                {
                    Key = "layout.detection.model_unavailable",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "layout", "warning" }
                });
                return signals;
            }

            // Run detection
            var detections = await DetectLayoutAsync(imagePath, session, ct);

            sw.Stop();

            // Emit timing signal
            signals.Add(new Signal
            {
                Key = "layout.detection.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "layout", "timing", "benchmark" }
            });

            if (detections.Count == 0)
            {
                signals.Add(new Signal
                {
                    Key = "layout.detection.no_elements",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "layout" }
                });
                return signals;
            }

            // Group detections by class
            var groupedDetections = detections
                .GroupBy(d => d.ClassName)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Emit all detections
            signals.Add(new Signal
            {
                Key = "layout.detection.elements",
                Value = detections,
                Confidence = detections.Average(d => d.Confidence),
                Source = Name,
                Tags = new List<string> { "layout", SignalTags.Content },
                Metadata = new Dictionary<string, object>
                {
                    ["total_count"] = detections.Count,
                    ["classes_detected"] = groupedDetections.Keys.ToArray(),
                    ["duration_ms"] = sw.ElapsedMilliseconds
                }
            });

            // Emit per-class signals for easy querying
            foreach (var (className, classDetections) in groupedDetections)
            {
                var normalizedClassName = className.ToLowerInvariant().Replace("-", "_");
                signals.Add(new Signal
                {
                    Key = $"layout.{normalizedClassName}.regions",
                    Value = classDetections.Select(d => new
                    {
                        d.X,
                        d.Y,
                        d.Width,
                        d.Height,
                        d.Confidence
                    }).ToList(),
                    Confidence = classDetections.Average(d => d.Confidence),
                    Source = Name,
                    Tags = new List<string> { "layout", normalizedClassName },
                    Metadata = new Dictionary<string, object>
                    {
                        ["count"] = classDetections.Count
                    }
                });
            }

            // Emit summary statistics
            signals.Add(new Signal
            {
                Key = "layout.detection.summary",
                Value = new
                {
                    TotalElements = detections.Count,
                    TextBlocks = groupedDetections.GetValueOrDefault("Text")?.Count ?? 0,
                    Tables = groupedDetections.GetValueOrDefault("Table")?.Count ?? 0,
                    Figures = groupedDetections.GetValueOrDefault("Picture")?.Count ?? 0,
                    Titles = groupedDetections.GetValueOrDefault("Title")?.Count ?? 0,
                    Lists = groupedDetections.GetValueOrDefault("List-item")?.Count ?? 0
                },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "layout", "statistics" }
            });

            // Cache text regions for OCR guidance
            var textRegions = detections
                .Where(d => d.ClassName is "Text" or "Title" or "Caption" or "Section-header")
                .Select(d => new Dictionary<string, int>
                {
                    ["x"] = d.X,
                    ["y"] = d.Y,
                    ["width"] = d.Width,
                    ["height"] = d.Height
                })
                .ToList();

            if (textRegions.Count > 0)
            {
                context.SetCached("layout.text_regions", textRegions);
                _logger?.LogInformation(
                    "Layout detection found {TextRegions} text regions, {Tables} tables, {Figures} figures",
                    textRegions.Count,
                    groupedDetections.GetValueOrDefault("Table")?.Count ?? 0,
                    groupedDetections.GetValueOrDefault("Picture")?.Count ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Layout detection failed");
            signals.Add(new Signal
            {
                Key = "layout.detection.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "layout", "error" }
            });
        }

        return signals;
    }

    private async Task<InferenceSession?> GetOrLoadModelAsync(CancellationToken ct)
    {
        if (_layoutSession != null) return _layoutSession;

        var modelPath = await GetOrDownloadModelAsync(ct);
        if (modelPath == null) return null;

        lock (_modelLock)
        {
            if (_layoutSession != null) return _layoutSession;

            try
            {
                _logger?.LogInformation("Loading YOLOv10-DocLayNet model from {Path}", modelPath);

                if (_sessionFactory != null)
                {
                    var provider = _sessionFactory.GetEffectiveProvider();
                    _logger?.LogInformation("Layout detection using ONNX execution provider: {Provider}", provider);
                    _layoutSession = _sessionFactory.CreateSession(modelPath);
                }
                else
                {
                    var sessionOptions = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };
                    _layoutSession = new InferenceSession(modelPath, sessionOptions);
                }

                _logger?.LogInformation("YOLOv10-DocLayNet model loaded successfully");
                return _layoutSession;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load layout detection model");
                return null;
            }
        }
    }

    private async Task<string?> GetOrDownloadModelAsync(CancellationToken ct)
    {
        // Try to get from ModelDownloader (auto-downloads if needed)
        if (_modelDownloader != null)
            try
            {
                _logger?.LogInformation("Checking/downloading YOLOv10-DocLayNet model (~62MB on first run)...");
                var path = await _modelDownloader.GetModelPathAsync(ModelType.YoloDocLayNet, ct);
                if (path != null && File.Exists(path))
                {
                    _logger?.LogDebug("Layout model ready at: {Path}", path);
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to auto-download layout detection model");
            }

        // Fallback to checking default paths
        var fallbackPaths = new[]
        {
            Path.Combine(_config.ModelsDirectory ?? "./models", "yolov10m-doclaynet.onnx"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LucidRAG", "models", "yolov10m-doclaynet.onnx")
        };

        foreach (var path in fallbackPaths)
            if (File.Exists(path))
                return path;

        _logger?.LogDebug("Layout model not found in any default location");
        return null;
    }

    private async Task<List<LayoutDetection>> DetectLayoutAsync(
        string imagePath,
        InferenceSession session,
        CancellationToken ct)
    {
        // Load and preprocess image
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);
        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // Resize to model input size (letterbox padding to preserve aspect ratio)
        var (resizedImage, scale, padX, padY) = ResizeWithLetterbox(image);

        // Convert to tensor (NCHW format)
        var tensor = CreateInputTensor(resizedImage);

        // Run inference
        var inputName = session.InputNames.FirstOrDefault() ?? "images";
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = session.Run(inputs);
        var output = results.FirstOrDefault()?.AsTensor<float>();

        if (output == null)
        {
            _logger?.LogWarning("Layout detection returned null output");
            return new List<LayoutDetection>();
        }

        // Parse YOLO output
        var detections = ParseYoloOutput(output, scale, padX, padY, originalWidth, originalHeight);

        // Apply NMS
        detections = ApplyNms(detections);

        return detections;
    }

    private (Image<Rgb24> Image, float Scale, int PadX, int PadY) ResizeWithLetterbox(Image<Rgb24> image)
    {
        var scale = Math.Min(
            (float)ModelInputSize / image.Width,
            (float)ModelInputSize / image.Height);

        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);

        var padX = (ModelInputSize - newWidth) / 2;
        var padY = (ModelInputSize - newHeight) / 2;

        // Create letterboxed image with gray padding
        var letterboxed = new Image<Rgb24>(ModelInputSize, ModelInputSize, new Rgb24(114, 114, 114));

        image.Mutate(x => x.Resize(newWidth, newHeight));

        letterboxed.Mutate(x => x.DrawImage(image, new Point(padX, padY), 1.0f));

        return (letterboxed, scale, padX, padY);
    }

    private DenseTensor<float> CreateInputTensor(Image<Rgb24> image)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, ModelInputSize, ModelInputSize });

        for (var y = 0; y < ModelInputSize; y++)
        for (var x = 0; x < ModelInputSize; x++)
        {
            var pixel = image[x, y];
            tensor[0, 0, y, x] = pixel.R / 255f;
            tensor[0, 1, y, x] = pixel.G / 255f;
            tensor[0, 2, y, x] = pixel.B / 255f;
        }

        return tensor;
    }

    private List<LayoutDetection> ParseYoloOutput(
        Tensor<float> output,
        float scale,
        int padX,
        int padY,
        int originalWidth,
        int originalHeight)
    {
        var detections = new List<LayoutDetection>();

        // YOLOv10 output shape: [1, num_detections, 4 + num_classes] or [1, 4 + num_classes, num_detections]
        var dims = output.Dimensions.ToArray();

        // Determine output format
        int numDetections;
        int stride;
        bool isTransposed;

        if (dims.Length == 3)
        {
            if (dims[2] == 4 + ClassNames.Length || dims[2] > dims[1])
            {
                // Shape: [1, num_detections, 4 + classes]
                numDetections = dims[1];
                stride = dims[2];
                isTransposed = false;
            }
            else
            {
                // Shape: [1, 4 + classes, num_detections]
                numDetections = dims[2];
                stride = dims[1];
                isTransposed = true;
            }
        }
        else
        {
            _logger?.LogWarning("Unexpected output shape: {Shape}", string.Join(", ", dims));
            return detections;
        }

        for (var i = 0; i < numDetections; i++)
        {
            float cx, cy, w, h;
            var classScores = new float[ClassNames.Length];

            if (isTransposed)
            {
                // [1, features, detections]
                cx = output[0, 0, i];
                cy = output[0, 1, i];
                w = output[0, 2, i];
                h = output[0, 3, i];
                for (var c = 0; c < ClassNames.Length && c + 4 < stride; c++) classScores[c] = output[0, c + 4, i];
            }
            else
            {
                // [1, detections, features]
                cx = output[0, i, 0];
                cy = output[0, i, 1];
                w = output[0, i, 2];
                h = output[0, i, 3];
                for (var c = 0; c < ClassNames.Length && c + 4 < stride; c++) classScores[c] = output[0, i, c + 4];
            }

            // Find best class
            var maxScore = classScores.Max();
            var classIdx = Array.IndexOf(classScores, maxScore);

            if (maxScore < ConfidenceThreshold || classIdx < 0 || classIdx >= ClassNames.Length)
                continue;

            // Convert from model coordinates to original image coordinates
            var x1 = (cx - w / 2 - padX) / scale;
            var y1 = (cy - h / 2 - padY) / scale;
            var x2 = (cx + w / 2 - padX) / scale;
            var y2 = (cy + h / 2 - padY) / scale;

            // Clamp to image bounds
            x1 = Math.Max(0, Math.Min(originalWidth, x1));
            y1 = Math.Max(0, Math.Min(originalHeight, y1));
            x2 = Math.Max(0, Math.Min(originalWidth, x2));
            y2 = Math.Max(0, Math.Min(originalHeight, y2));

            var width = x2 - x1;
            var height = y2 - y1;

            if (width > 5 && height > 5) // Skip tiny detections
                detections.Add(new LayoutDetection
                {
                    ClassName = ClassNames[classIdx],
                    ClassIndex = classIdx,
                    Confidence = maxScore,
                    X = (int)x1,
                    Y = (int)y1,
                    Width = (int)width,
                    Height = (int)height
                });
        }

        return detections;
    }

    private List<LayoutDetection> ApplyNms(List<LayoutDetection> detections)
    {
        if (detections.Count <= 1)
            return detections;

        var result = new List<LayoutDetection>();

        // Group by class and apply NMS per class
        var byClass = detections.GroupBy(d => d.ClassIndex);

        foreach (var classGroup in byClass)
        {
            var classDetections = classGroup.OrderByDescending(d => d.Confidence).ToList();

            while (classDetections.Count > 0)
            {
                var best = classDetections[0];
                result.Add(best);
                classDetections.RemoveAt(0);

                classDetections.RemoveAll(d => CalculateIoU(best, d) > NmsThreshold);
            }
        }

        return result;
    }

    private static float CalculateIoU(LayoutDetection a, LayoutDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (x2 <= x1 || y2 <= y1)
            return 0;

        var intersection = (x2 - x1) * (y2 - y1);
        var areaA = a.Width * a.Height;
        var areaB = b.Width * b.Height;
        var union = areaA + areaB - intersection;

        return union > 0 ? (float)intersection / union : 0;
    }
}

/// <summary>
///     Detected layout element with bounding box and class information.
/// </summary>
public record LayoutDetection
{
    /// <summary>Class name (Text, Title, Table, etc.)</summary>
    public required string ClassName { get; init; }

    /// <summary>Class index (0-10)</summary>
    public int ClassIndex { get; init; }

    /// <summary>Detection confidence (0.0-1.0)</summary>
    public float Confidence { get; init; }

    /// <summary>Bounding box X coordinate</summary>
    public int X { get; init; }

    /// <summary>Bounding box Y coordinate</summary>
    public int Y { get; init; }

    /// <summary>Bounding box width</summary>
    public int Width { get; init; }

    /// <summary>Bounding box height</summary>
    public int Height { get; init; }
}
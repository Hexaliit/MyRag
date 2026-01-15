using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Layout;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Layout Routing Wave - Extracts and routes detected layout regions to appropriate processors.
/// - Tables → DataSummarizer for structured extraction
/// - Pictures/Figures → ImageSummarizer for recursive analysis
/// - Text regions → OCR validation
///
/// Priority: 63 (runs right after LayoutDetectionWave at 64, before OCR at 60).
/// </summary>
public class LayoutRoutingWave : IAnalysisWave
{
    private readonly ImageConfig _config;
    private readonly LayoutRegionExtractor _extractor;
    private readonly ILogger<LayoutRoutingWave>? _logger;

    public string Name => "LayoutRoutingWave";
    public int Priority => 63; // After LayoutDetectionWave (64), before OCR waves (60)
    public IReadOnlyList<string> Tags => new[] { "layout", "routing", "extraction", SignalTags.Content };

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Only run if layout detection found elements
        if (!context.HasSignal("layout.detection.elements"))
            return false;

        // Skip if routing is disabled
        if (!_config.EnableLayoutRouting)
            return false;

        return true;
    }

    public LayoutRoutingWave(
        IOptions<ImageConfig> config,
        LayoutRegionExtractor extractor,
        ILogger<LayoutRoutingWave>? logger = null)
    {
        _config = config.Value;
        _extractor = extractor;
        _logger = logger;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var detections = context.GetValue<List<LayoutDetection>>("layout.detection.elements");
        if (detections == null || detections.Count == 0)
        {
            return signals;
        }

        _logger?.LogInformation("Routing {Count} layout regions from {Image}",
            detections.Count, Path.GetFileName(imagePath));

        try
        {
            // Extract table regions for DataSummarizer
            var tableRegions = await ExtractAndRouteTablesAsync(imagePath, detections, context, signals, ct);

            // Extract picture regions for recursive ImageSummarizer
            var pictureRegions = await ExtractAndRoutePicturesAsync(imagePath, detections, context, signals, ct);

            // Use text regions for OCR guidance/validation
            await ProcessTextRegionsAsync(detections, context, signals, ct);

            sw.Stop();

            // Emit routing summary
            signals.Add(new Signal
            {
                Key = "layout.routing.summary",
                Value = new
                {
                    TablesExtracted = tableRegions.Count,
                    PicturesExtracted = pictureRegions.Count,
                    TextRegionsProcessed = detections.Count(d => d.ClassName is "Text" or "Title"),
                    DurationMs = sw.ElapsedMilliseconds
                },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "layout", "routing", "summary" }
            });

            signals.Add(new Signal
            {
                Key = "layout.routing.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "layout", "timing" }
            });

            _logger?.LogInformation(
                "Layout routing complete: {Tables} tables, {Pictures} pictures in {Ms}ms",
                tableRegions.Count, pictureRegions.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Layout routing failed");
            signals.Add(new Signal
            {
                Key = "layout.routing.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "layout", "error" }
            });
        }

        return signals;
    }

    /// <summary>
    /// Extract table regions and prepare for DataSummarizer processing.
    /// </summary>
    private async Task<List<ExtractedRegion>> ExtractAndRouteTablesAsync(
        string imagePath,
        List<LayoutDetection> detections,
        AnalysisContext context,
        List<Signal> signals,
        CancellationToken ct)
    {
        var tableDetections = detections.Where(d => d.ClassName == "Table").ToList();
        if (tableDetections.Count == 0)
            return new List<ExtractedRegion>();

        _logger?.LogInformation("Extracting {Count} table regions", tableDetections.Count);

        var extracted = await _extractor.ExtractRegionsAsync(
            imagePath, tableDetections, new[] { "Table" }, ct);

        if (extracted.Count > 0)
        {
            // Emit extracted table paths for downstream processing
            signals.Add(new Signal
            {
                Key = "layout.tables.extracted",
                Value = extracted.Select(e => new
                {
                    Path = e.ExtractedImagePath,
                    e.OriginalBounds,
                    e.Confidence,
                    e.RegionIndex
                }).ToList(),
                Confidence = extracted.Average(e => e.Confidence),
                Source = Name,
                Tags = new List<string> { "layout", "table", "extracted" },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = extracted.Count,
                    ["source_image"] = imagePath
                }
            });

            // Cache paths for DataSummarizer integration
            context.SetCached("layout.table_image_paths", extracted.Select(e => e.ExtractedImagePath).ToList());

            // Emit individual table signals for easy access
            for (int i = 0; i < extracted.Count; i++)
            {
                signals.Add(new Signal
                {
                    Key = $"layout.table.{i}.path",
                    Value = extracted[i].ExtractedImagePath,
                    Confidence = extracted[i].Confidence,
                    Source = Name,
                    Tags = new List<string> { "layout", "table", "path" }
                });
            }
        }

        return extracted;
    }

    /// <summary>
    /// Extract picture/figure regions for recursive ImageSummarizer processing.
    /// </summary>
    private async Task<List<ExtractedRegion>> ExtractAndRoutePicturesAsync(
        string imagePath,
        List<LayoutDetection> detections,
        AnalysisContext context,
        List<Signal> signals,
        CancellationToken ct)
    {
        var pictureDetections = detections.Where(d => d.ClassName == "Picture").ToList();
        if (pictureDetections.Count == 0)
            return new List<ExtractedRegion>();

        _logger?.LogInformation("Extracting {Count} picture/figure regions", pictureDetections.Count);

        var extracted = await _extractor.ExtractRegionsAsync(
            imagePath, pictureDetections, new[] { "Picture" }, ct);

        if (extracted.Count > 0)
        {
            // Emit extracted picture paths for recursive ImageSummarizer
            signals.Add(new Signal
            {
                Key = "layout.pictures.extracted",
                Value = extracted.Select(e => new
                {
                    Path = e.ExtractedImagePath,
                    e.OriginalBounds,
                    e.Confidence,
                    e.RegionIndex
                }).ToList(),
                Confidence = extracted.Average(e => e.Confidence),
                Source = Name,
                Tags = new List<string> { "layout", "picture", "extracted" },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = extracted.Count,
                    ["source_image"] = imagePath,
                    ["needs_recursive_analysis"] = true
                }
            });

            // Cache paths for recursive ImageSummarizer processing
            context.SetCached("layout.picture_image_paths", extracted.Select(e => e.ExtractedImagePath).ToList());

            // Emit individual picture signals
            for (int i = 0; i < extracted.Count; i++)
            {
                signals.Add(new Signal
                {
                    Key = $"layout.picture.{i}.path",
                    Value = extracted[i].ExtractedImagePath,
                    Confidence = extracted[i].Confidence,
                    Source = Name,
                    Tags = new List<string> { "layout", "picture", "path" }
                });
            }
        }

        return extracted;
    }

    /// <summary>
    /// Process text regions for OCR guidance and validation.
    /// </summary>
    private Task ProcessTextRegionsAsync(
        List<LayoutDetection> detections,
        AnalysisContext context,
        List<Signal> signals,
        CancellationToken ct)
    {
        var textRegions = detections
            .Where(d => d.ClassName is "Text" or "Title" or "Caption" or "Section-header" or "List-item")
            .ToList();

        if (textRegions.Count == 0)
            return Task.CompletedTask;

        // Calculate total text area coverage
        var totalArea = textRegions.Sum(r => r.Width * r.Height);
        var avgConfidence = textRegions.Average(r => r.Confidence);

        // Emit text region bounds for OCR targeting
        signals.Add(new Signal
        {
            Key = "layout.text_regions",
            Value = textRegions.Select(r => new
            {
                Type = r.ClassName,
                r.X,
                r.Y,
                r.Width,
                r.Height,
                r.Confidence
            }).ToList(),
            Confidence = avgConfidence,
            Source = Name,
            Tags = new List<string> { "layout", "text", "regions" },
            Metadata = new Dictionary<string, object>
            {
                ["count"] = textRegions.Count,
                ["total_area"] = totalArea,
                ["titles"] = textRegions.Count(r => r.ClassName == "Title"),
                ["paragraphs"] = textRegions.Count(r => r.ClassName == "Text")
            }
        });

        // Cache for OCR validation
        context.SetCached("layout.text_region_bounds", textRegions.Select(r => new
        {
            r.X,
            r.Y,
            r.Width,
            r.Height,
            r.ClassName
        }).ToList());

        return Task.CompletedTask;
    }
}

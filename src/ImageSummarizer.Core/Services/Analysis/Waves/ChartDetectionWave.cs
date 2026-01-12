using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Vision;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
///     Chart Detection Wave - Specialized zero-shot classification for chart/graph images.
///     Uses CLIP text encoder with chart-specific labels to identify and classify data visualizations.
///     Produces chart.detected, chart.type signals for downstream data extraction.
///     Priority: 47 (runs after general CLIP classification)
/// </summary>
public class ChartDetectionWave : IAnalysisWave
{
    private readonly ClipZeroShotService _clipService;
    private readonly ImageConfig _config;
    private readonly ILogger<ChartDetectionWave>? _logger;

    /// <summary>
    ///     Chart-specific labels for CLIP zero-shot classification.
    ///     Natural language descriptions that CLIP text encoder can match against image embeddings.
    /// </summary>
    private static readonly string[] ChartLabels =
    [
        "a bar chart or bar graph showing data with vertical or horizontal bars",
        "a line chart or line graph with connected data points over time",
        "a pie chart or donut chart with circular segments showing proportions",
        "a scatter plot or scatter chart with individual data points",
        "an area chart with filled regions under lines",
        "a histogram showing frequency distribution with bars",
        "a box plot or box-and-whisker chart showing statistical distribution",
        "a heatmap with color-coded grid cells",
        "a radar chart or spider chart with radial axes",
        "a treemap with nested rectangles",
        "a photograph or natural image without charts",
        "a diagram or flowchart without data visualization",
        "a screenshot of software or user interface",
        "a text document or scanned page"
    ];

    /// <summary>
    ///     Mapping from chart labels to ChartType enum values.
    /// </summary>
    private static readonly Dictionary<string, ChartType> LabelToChartType = new()
    {
        ["a bar chart or bar graph showing data with vertical or horizontal bars"] = ChartType.BarChart,
        ["a line chart or line graph with connected data points over time"] = ChartType.LineChart,
        ["a pie chart or donut chart with circular segments showing proportions"] = ChartType.PieChart,
        ["a scatter plot or scatter chart with individual data points"] = ChartType.ScatterPlot,
        ["an area chart with filled regions under lines"] = ChartType.AreaChart,
        ["a histogram showing frequency distribution with bars"] = ChartType.Histogram,
        ["a box plot or box-and-whisker chart showing statistical distribution"] = ChartType.BoxPlot,
        ["a heatmap with color-coded grid cells"] = ChartType.Heatmap,
        ["a radar chart or spider chart with radial axes"] = ChartType.RadarChart,
        ["a treemap with nested rectangles"] = ChartType.Treemap
    };

    public string Name => "ChartDetectionWave";
    public int Priority => 47; // After ClipClassificationWave (46)
    public IReadOnlyList<string> Tags => [SignalTags.Content, "chart", "detection", "clip"];

    public ChartDetectionWave(
        ClipZeroShotService clipService,
        IOptions<ImageConfig> config,
        ILogger<ChartDetectionWave>? logger = null)
    {
        _clipService = clipService;
        _config = config.Value;
        _logger = logger;
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if CLIP embedding is disabled
        if (!_config.EnableClipEmbedding)
            return false;

        // Skip if auto-routing says to skip
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Only run if we have a CLIP embedding from ClipEmbeddingWave
        if (!context.HasSignal("vision.clip.embedding"))
        {
            _logger?.LogDebug("Skipping ChartDetectionWave: no CLIP embedding available");
            return false;
        }

        // Optionally: Skip if already confidently classified as non-chart
        var imageType = context.GetValue<string>("clip.entity_type");
        if (imageType is "person" or "animal" or "landscape")
        {
            _logger?.LogDebug("Skipping ChartDetectionWave: image classified as {Type}", imageType);
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

        try
        {
            // Get the CLIP embedding from context
            var embedding = context.GetValue<float[]>("vision.clip.embedding");
            if (embedding == null || embedding.Length == 0)
            {
                _logger?.LogDebug("No valid CLIP embedding in context");
                return signals;
            }

            // Check if service is available
            if (!await _clipService.IsAvailableAsync(ct))
            {
                signals.Add(new Signal
                {
                    Key = "chart.detection.unavailable",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = ["chart", "status"]
                });
                return signals;
            }

            // Classify with chart-specific labels
            var classifications = await _clipService.ClassifyWithLabelsAsync(
                embedding,
                ChartLabels,
                topK: 5,
                ct: ct);

            if (classifications.Count == 0)
            {
                signals.Add(new Signal
                {
                    Key = "chart.detection.no_matches",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = ["chart", "detection"]
                });
                return signals;
            }

            // Get top match
            var topMatch = classifications.First();
            var isChart = LabelToChartType.ContainsKey(topMatch.Label);
            var chartType = isChart ? LabelToChartType[topMatch.Label] : ChartType.Unknown;

            // Emit classification result
            signals.Add(new Signal
            {
                Key = "chart.clip_classification",
                Value = topMatch.Label,
                Confidence = topMatch.Confidence,
                Source = Name,
                Tags = ["chart", "classification"],
                Metadata = new Dictionary<string, object>
                {
                    ["all_classifications"] = classifications.Select(c => new
                    {
                        label = c.Label,
                        confidence = c.Confidence,
                        rank = c.Rank
                    }).ToList()
                }
            });

            // Emit chart detection result
            signals.Add(new Signal
            {
                Key = "chart.detected",
                Value = isChart,
                Confidence = topMatch.Confidence,
                Source = Name,
                Tags = ["chart", "detection"]
            });

            if (isChart)
            {
                // High-confidence chart detection
                signals.Add(new Signal
                {
                    Key = "chart.type",
                    Value = chartType.ToString(),
                    Confidence = topMatch.Confidence,
                    Source = Name,
                    Tags = ["chart", "type"],
                    Metadata = new Dictionary<string, object>
                    {
                        ["chart_type_enum"] = (int)chartType,
                        ["label"] = topMatch.Label
                    }
                });

                signals.Add(new Signal
                {
                    Key = "chart.detection_method",
                    Value = "clip_zero_shot",
                    Confidence = 1.0,
                    Source = Name,
                    Tags = ["chart", "method"]
                });

                // Check if Vision LLM extraction is needed (for data extraction)
                // Request extraction if confidence is reasonably high
                if (topMatch.Confidence > 0.3)
                {
                    signals.Add(new Signal
                    {
                        Key = "chart.needs_extraction",
                        Value = true,
                        Confidence = topMatch.Confidence,
                        Source = Name,
                        Tags = ["chart", "extraction"],
                        Metadata = new Dictionary<string, object>
                        {
                            ["chart_type"] = chartType.ToString(),
                            ["extraction_priority"] = topMatch.Confidence > 0.6 ? "high" : "medium"
                        }
                    });
                }

                _logger?.LogInformation(
                    "Chart detected: {ChartType} ({Confidence:P0})",
                    chartType,
                    topMatch.Confidence);
            }
            else
            {
                // Not a chart - emit for tracking
                signals.Add(new Signal
                {
                    Key = "chart.not_chart",
                    Value = topMatch.Label,
                    Confidence = topMatch.Confidence,
                    Source = Name,
                    Tags = ["chart", "negative"]
                });

                _logger?.LogDebug("Not a chart: {Classification} ({Confidence:P0})",
                    topMatch.Label, topMatch.Confidence);
            }

            // Check for low-confidence results that may need Vision LLM verification
            if (topMatch.Confidence < 0.4 && isChart)
            {
                signals.Add(new Signal
                {
                    Key = "chart.needs_verification",
                    Value = true,
                    Confidence = 1 - topMatch.Confidence,
                    Source = Name,
                    Tags = ["chart", "verification"],
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "low_confidence",
                        ["suggested_method"] = "vision_llm"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Chart detection wave failed");
            signals.Add(new Signal
            {
                Key = "chart.detection.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = ["chart", "error"]
            });
        }

        return signals;
    }
}

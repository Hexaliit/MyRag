using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
///     Chart Extraction Wave - Extracts structured data from detected charts using Vision LLM.
///     Only runs when chart.detected is true and chart.needs_extraction is set.
///     Uses type-specific prompts for bar charts, pie charts, line charts, etc.
///     Priority: 48 (runs after ChartDetectionWave)
/// </summary>
public class ChartExtractionWave : IAnalysisWave
{
    private readonly IOptions<ImageConfig> _configOptions;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ChartExtractionWave>? _logger;

    public ChartExtractionWave(
        IOptions<ImageConfig> config,
        ILogger<ChartExtractionWave>? logger = null,
        HttpClient? httpClient = null)
    {
        _configOptions = config;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    private ImageConfig Config => _configOptions.Value;

    public string Name => "ChartExtractionWave";
    public int Priority => 48; // After ChartDetectionWave (47)
    public IReadOnlyList<string> Tags => [SignalTags.Content, "chart", "extraction", "llm"];

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if Vision LLM is disabled
        if (!Config.EnableVisionLlm)
            return false;

        // Skip if auto-routing says to skip
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Only run if chart was detected and extraction is requested
        var chartDetected = context.GetValue<bool>("chart.detected");
        var needsExtraction = context.GetValue<bool>("chart.needs_extraction");

        if (!chartDetected)
        {
            _logger?.LogDebug("Skipping ChartExtractionWave: no chart detected");
            return false;
        }

        if (!needsExtraction)
        {
            _logger?.LogDebug("Skipping ChartExtractionWave: extraction not requested");
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
            // Get chart type from detection wave
            var chartTypeStr = context.GetValue<string>("chart.type");
            if (!Enum.TryParse<ChartType>(chartTypeStr, out var chartType))
                chartType = ChartType.Unknown;

            _logger?.LogInformation("Extracting data from {ChartType}", chartType);

            // Convert image to base64
            var imageBase64 = await ConvertImageToBase64Async(imagePath, ct);
            if (string.IsNullOrEmpty(imageBase64))
            {
                signals.Add(CreateErrorSignal("Failed to read image"));
                return signals;
            }

            // Build type-specific extraction prompt
            var prompt = GetExtractionPrompt(chartType);

            // Query Vision LLM
            var response = await QueryVisionLlmAsync(imageBase64, prompt, ct);
            if (string.IsNullOrEmpty(response))
            {
                signals.Add(CreateErrorSignal("Vision LLM returned empty response"));
                return signals;
            }

            // Parse the response into structured data
            var chartData = ParseChartResponse(response, chartType, imagePath);

            if (chartData != null)
            {
                // Emit extracted chart data
                signals.Add(new Signal
                {
                    Key = "chart.data",
                    Value = chartData.ToDictionary(),
                    Confidence = chartData.Confidence,
                    Source = Name,
                    Tags = ["chart", "data", "extracted"],
                    Metadata = new Dictionary<string, object>
                    {
                        ["chart_type"] = chartType.ToString(),
                        ["data_points"] = chartData.TotalDataPoints,
                        ["series_count"] = chartData.Series.Count
                    }
                });

                // Emit CSV representation for easy consumption
                var csv = chartData.ToCsv();
                if (!string.IsNullOrEmpty(csv))
                    signals.Add(new Signal
                    {
                        Key = "chart.data.csv",
                        Value = csv,
                        Confidence = chartData.Confidence,
                        Source = Name,
                        Tags = ["chart", "data", "csv"]
                    });

                // Emit summary
                signals.Add(new Signal
                {
                    Key = "chart.data.summary",
                    Value = chartData.Summary,
                    Confidence = chartData.Confidence,
                    Source = Name,
                    Tags = ["chart", "summary"]
                });

                // Emit title if found
                if (!string.IsNullOrEmpty(chartData.Title))
                    signals.Add(new Signal
                    {
                        Key = "chart.title",
                        Value = chartData.Title,
                        Confidence = chartData.Confidence,
                        Source = Name,
                        Tags = ["chart", "title"]
                    });

                _logger?.LogInformation(
                    "Extracted {DataPoints} data points from {ChartType}: {Summary}",
                    chartData.TotalDataPoints,
                    chartType,
                    chartData.Summary);
            }
            else
            {
                // Store raw response for debugging
                signals.Add(new Signal
                {
                    Key = "chart.extraction.raw_response",
                    Value = response,
                    Confidence = 0.5,
                    Source = Name,
                    Tags = ["chart", "raw", "debug"]
                });

                signals.Add(new Signal
                {
                    Key = "chart.extraction.parse_failed",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = ["chart", "error"]
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Chart extraction failed");
            signals.Add(CreateErrorSignal(ex.Message));
        }

        return signals;
    }

    /// <summary>
    ///     Get the extraction prompt tailored to the chart type.
    /// </summary>
    private static string GetExtractionPrompt(ChartType chartType)
    {
        var baseInstruction = """
                              You are analyzing a chart/graph image. Extract all visible data as accurately as possible.
                              Respond ONLY with valid JSON - no markdown, no explanation, just the JSON object.
                              If you cannot read a value precisely, estimate it based on the axis scale.
                              """;

        var typeSpecificPrompt = chartType switch
        {
            ChartType.BarChart => """
                                  This is a BAR CHART. Extract:
                                  1. The chart title (if visible)
                                  2. X-axis label and Y-axis label (if visible)
                                  3. For each bar: the category/label and its value

                                  Respond with this exact JSON structure:
                                  {
                                      "title": "Chart Title",
                                      "x_label": "X Axis Label",
                                      "y_label": "Y Axis Label",
                                      "data": [
                                          {"category": "Category 1", "value": 123.45},
                                          {"category": "Category 2", "value": 67.89}
                                      ]
                                  }
                                  """,

            ChartType.PieChart => """
                                  This is a PIE CHART. Extract:
                                  1. The chart title (if visible)
                                  2. For each segment: the label and its value/percentage

                                  Respond with this exact JSON structure:
                                  {
                                      "title": "Chart Title",
                                      "data": [
                                          {"label": "Segment 1", "value": 25.5},
                                          {"label": "Segment 2", "value": 30.0}
                                      ]
                                  }
                                  Note: Values should be percentages if shown, otherwise absolute values.
                                  """,

            ChartType.LineChart => """
                                   This is a LINE CHART. Extract:
                                   1. The chart title (if visible)
                                   2. X-axis label and Y-axis label (if visible)
                                   3. For each line/series: the series name and its data points

                                   Respond with this exact JSON structure:
                                   {
                                       "title": "Chart Title",
                                       "x_label": "X Axis Label",
                                       "y_label": "Y Axis Label",
                                       "series": [
                                           {
                                               "name": "Series 1",
                                               "points": [
                                                   {"x": "Jan", "y": 100},
                                                   {"x": "Feb", "y": 150}
                                               ]
                                           }
                                       ]
                                   }
                                   """,

            ChartType.ScatterPlot => """
                                     This is a SCATTER PLOT. Extract:
                                     1. The chart title (if visible)
                                     2. X-axis label and Y-axis label (if visible)
                                     3. Data points as (x, y) coordinates

                                     Respond with this exact JSON structure:
                                     {
                                         "title": "Chart Title",
                                         "x_label": "X Axis Label",
                                         "y_label": "Y Axis Label",
                                         "points": [
                                             {"x": 1.5, "y": 2.3},
                                             {"x": 3.2, "y": 4.1}
                                         ]
                                     }
                                     """,

            ChartType.Histogram => """
                                   This is a HISTOGRAM. Extract:
                                   1. The chart title (if visible)
                                   2. X-axis label (variable) and Y-axis label (frequency)
                                   3. For each bin: the range and frequency count

                                   Respond with this exact JSON structure:
                                   {
                                       "title": "Chart Title",
                                       "x_label": "Variable",
                                       "y_label": "Frequency",
                                       "bins": [
                                           {"range": "0-10", "count": 5},
                                           {"range": "10-20", "count": 12}
                                       ]
                                   }
                                   """,

            _ => """
                 Extract all visible data from this chart/visualization.
                 Include the title, axis labels, legend entries, and all data values.

                 Respond with this JSON structure:
                 {
                     "title": "Chart Title",
                     "type": "detected chart type",
                     "x_label": "X Axis Label",
                     "y_label": "Y Axis Label",
                     "data": [...]
                 }
                 """
        };

        return $"{baseInstruction}\n\n{typeSpecificPrompt}";
    }

    /// <summary>
    ///     Parse the Vision LLM response into structured chart data.
    /// </summary>
    private ExtractedChartData? ParseChartResponse(string response, ChartType chartType, string sourcePath)
    {
        try
        {
            // Try to extract JSON from the response (in case there's extra text)
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
            {
                _logger?.LogWarning("No valid JSON found in response");
                return null;
            }

            var jsonStr = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            var chartData = new ExtractedChartData
            {
                Type = chartType,
                Title = GetStringProperty(root, "title"),
                XLabel = GetStringProperty(root, "x_label"),
                YLabel = GetStringProperty(root, "y_label"),
                Confidence = 0.8, // Base confidence for LLM extraction
                ExtractionMethod = "vision_llm",
                SourcePath = sourcePath,
                Series = []
            };

            // Parse data based on chart type
            switch (chartType)
            {
                case ChartType.BarChart:
                case ChartType.Histogram:
                    ParseBarChartData(root, chartData);
                    break;

                case ChartType.PieChart:
                    ParsePieChartData(root, chartData);
                    break;

                case ChartType.LineChart:
                case ChartType.AreaChart:
                    ParseLineChartData(root, chartData);
                    break;

                case ChartType.ScatterPlot:
                    ParseScatterPlotData(root, chartData);
                    break;

                default:
                    ParseGenericData(root, chartData);
                    break;
            }

            return chartData.Series.Count > 0 ? chartData : null;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse JSON from response");
            return null;
        }
    }

    private static void ParseBarChartData(JsonElement root, ExtractedChartData chartData)
    {
        if (!root.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return;

        var series = new ChartDataSeries { Name = "Values", Points = [] };
        var index = 0;

        foreach (var item in dataArray.EnumerateArray())
        {
            var category = GetStringProperty(item, "category") ??
                           GetStringProperty(item, "label") ?? $"Item {index + 1}";
            var value = GetDoubleProperty(item, "value") ?? GetDoubleProperty(item, "count") ?? 0;

            series.Points.Add(ChartDataPoint.FromCategory(category, value, index));
            index++;
        }

        if (series.Points.Count > 0)
            chartData.Series.Add(series);
    }

    private static void ParsePieChartData(JsonElement root, ExtractedChartData chartData)
    {
        if (!root.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return;

        var series = new ChartDataSeries { Name = "Segments", Points = [] };
        var index = 0;

        foreach (var item in dataArray.EnumerateArray())
        {
            var label = GetStringProperty(item, "label") ??
                        GetStringProperty(item, "category") ?? $"Segment {index + 1}";
            var value = GetDoubleProperty(item, "value") ?? GetDoubleProperty(item, "percentage") ?? 0;

            series.Points.Add(ChartDataPoint.FromCategory(label, value, index));
            index++;
        }

        if (series.Points.Count > 0)
            chartData.Series.Add(series);
    }

    private static void ParseLineChartData(JsonElement root, ExtractedChartData chartData)
    {
        if (!root.TryGetProperty("series", out var seriesArray) || seriesArray.ValueKind != JsonValueKind.Array)
        {
            // Fallback: try single series from "data"
            ParseBarChartData(root, chartData);
            return;
        }

        foreach (var seriesItem in seriesArray.EnumerateArray())
        {
            var seriesName = GetStringProperty(seriesItem, "name") ?? $"Series {chartData.Series.Count + 1}";
            var series = new ChartDataSeries { Name = seriesName, Points = [] };

            if (seriesItem.TryGetProperty("points", out var pointsArray) &&
                pointsArray.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var point in pointsArray.EnumerateArray())
                {
                    double x = index;
                    double y = 0;
                    string? label = null;

                    if (point.ValueKind == JsonValueKind.Object)
                    {
                        x = GetDoubleProperty(point, "x") ?? index;
                        y = GetDoubleProperty(point, "y") ?? 0;
                        label = GetStringProperty(point, "x"); // X might be a label
                    }
                    else if (point.ValueKind == JsonValueKind.Array)
                    {
                        var arr = point.EnumerateArray().ToArray();
                        if (arr.Length >= 2)
                        {
                            x = arr[0].TryGetDouble(out var xVal) ? xVal : index;
                            y = arr[1].TryGetDouble(out var yVal) ? yVal : 0;
                        }
                    }

                    series.Points.Add(new ChartDataPoint { X = x, Y = y, Label = label });
                    index++;
                }
            }

            if (series.Points.Count > 0)
                chartData.Series.Add(series);
        }
    }

    private static void ParseScatterPlotData(JsonElement root, ExtractedChartData chartData)
    {
        if (!root.TryGetProperty("points", out var pointsArray) || pointsArray.ValueKind != JsonValueKind.Array)
            // Fallback to "data"
            if (!root.TryGetProperty("data", out pointsArray) || pointsArray.ValueKind != JsonValueKind.Array)
                return;

        var series = new ChartDataSeries { Name = "Points", Points = [] };

        foreach (var point in pointsArray.EnumerateArray())
        {
            var x = GetDoubleProperty(point, "x") ?? 0;
            var y = GetDoubleProperty(point, "y") ?? 0;
            series.Points.Add(ChartDataPoint.From(x, y));
        }

        if (series.Points.Count > 0)
            chartData.Series.Add(series);
    }

    private static void ParseGenericData(JsonElement root, ExtractedChartData chartData)
    {
        // Try various common data formats
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array) ParseBarChartData(root, chartData);
        }
        else if (root.TryGetProperty("series", out _))
        {
            ParseLineChartData(root, chartData);
        }
        else if (root.TryGetProperty("points", out _))
        {
            ParseScatterPlotData(root, chartData);
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static double? GetDoubleProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDouble(),
            JsonValueKind.String when double.TryParse(prop.GetString(), out var d) => d,
            _ => null
        };
    }

    /// <summary>
    ///     Query the Vision LLM (Ollama) with an image and prompt.
    /// </summary>
    private async Task<string?> QueryVisionLlmAsync(string imageBase64, string prompt, CancellationToken ct)
    {
        try
        {
            var ollamaUrl = Config.OllamaBaseUrl ?? "http://localhost:11434";
            var model = Config.VisionLlmModel ?? "llava";

            var request = new
            {
                model,
                prompt,
                images = new[] { imageBase64 },
                stream = false,
                options = new
                {
                    temperature = 0.1, // Low temperature for more deterministic extraction
                    num_predict = 2048 // Enough tokens for chart data
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            _httpClient.Timeout = TimeSpan.FromSeconds(60);
            var response = await _httpClient.PostAsync($"{ollamaUrl}/api/generate", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning("Ollama request failed: {Status}, Body: {Body}",
                    response.StatusCode, errorBody?.Substring(0, Math.Min(200, errorBody?.Length ?? 0)));
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("response", out var responseText)) return responseText.GetString();

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to query Vision LLM for chart extraction");
            return null;
        }
    }

    /// <summary>
    ///     Convert image to base64 for Ollama API.
    /// </summary>
    private async Task<string?> ConvertImageToBase64Async(string imagePath, CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync(imagePath, ct);

            // Resize if too large (Ollama has limits)
            const int maxDimension = 1024;
            if (image.Width > maxDimension || image.Height > maxDimension)
            {
                var ratio = Math.Min((double)maxDimension / image.Width, (double)maxDimension / image.Height);
                var newWidth = (int)(image.Width * ratio);
                var newHeight = (int)(image.Height * ratio);
                image.Mutate(x => x.Resize(newWidth, newHeight));
            }

            using var ms = new MemoryStream();
            await image.SaveAsJpegAsync(ms, ct);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to convert image to base64: {Path}", imagePath);
            return null;
        }
    }

    private Signal CreateErrorSignal(string message)
    {
        return new Signal
        {
            Key = "chart.extraction.error",
            Value = message,
            Confidence = 0,
            Source = Name,
            Tags = ["chart", "error"]
        };
    }
}
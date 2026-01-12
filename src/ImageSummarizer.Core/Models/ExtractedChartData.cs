using System.Globalization;
using System.Text;

namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
///     Data extracted from a chart image via Vision LLM or specialized extractors.
/// </summary>
public class ExtractedChartData
{
    /// <summary>
    ///     The detected chart type
    /// </summary>
    public ChartType Type { get; init; }

    /// <summary>
    ///     Chart title if detected
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    ///     X-axis label if present
    /// </summary>
    public string? XLabel { get; init; }

    /// <summary>
    ///     Y-axis label if present
    /// </summary>
    public string? YLabel { get; init; }

    /// <summary>
    ///     Data series extracted from the chart
    /// </summary>
    public List<ChartDataSeries> Series { get; init; } = [];

    /// <summary>
    ///     Confidence score for the extraction (0.0 - 1.0)
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    ///     Method used for extraction (e.g., "vision_llm", "clip_classification", "opencv")
    /// </summary>
    public string? ExtractionMethod { get; init; }

    /// <summary>
    ///     Bounding box of the chart region [x, y, width, height] in normalized coordinates (0-1)
    /// </summary>
    public float[]? BoundingBox { get; init; }

    /// <summary>
    ///     Source path of the image
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>
    ///     Additional metadata about the extraction
    /// </summary>
    public Dictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    ///     Get total number of data points across all series
    /// </summary>
    public int TotalDataPoints => Series.Sum(s => s.Points.Count);

    /// <summary>
    ///     Generate a human-readable summary of the chart data
    /// </summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"{Type}");

            if (!string.IsNullOrEmpty(Title))
                sb.Append($" \"{Title}\"");

            if (Series.Count > 0)
            {
                sb.Append($" with {TotalDataPoints} data points");
                if (Series.Count > 1)
                    sb.Append($" in {Series.Count} series");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    ///     Convert the chart data to CSV format
    /// </summary>
    /// <param name="includeHeaders">Whether to include column headers</param>
    public string ToCsv(bool includeHeaders = true)
    {
        var sb = new StringBuilder();

        if (Series.Count == 0)
            return string.Empty;

        // For single series, output simple x,y columns
        if (Series.Count == 1)
        {
            var series = Series[0];
            if (includeHeaders)
            {
                var xHeader = XLabel ?? "X";
                var yHeader = YLabel ?? series.Name ?? "Y";
                sb.AppendLine($"{xHeader},{yHeader}");
            }

            foreach (var point in series.Points)
            {
                var xVal = point.Label ?? point.X.ToString(CultureInfo.InvariantCulture);
                sb.AppendLine($"{EscapeCsv(xVal)},{point.Y.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            // Multiple series: use series names as column headers
            if (includeHeaders)
            {
                var headers = new List<string> { XLabel ?? "X" };
                headers.AddRange(Series.Select(s => s.Name ?? $"Series {Series.IndexOf(s) + 1}"));
                sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
            }

            // Collect all unique X values and create pivot table
            var allXValues = Series
                .SelectMany(s => s.Points)
                .Select(p => p.Label ?? p.X.ToString(CultureInfo.InvariantCulture))
                .Distinct()
                .ToList();

            foreach (var xVal in allXValues)
            {
                var row = new List<string> { EscapeCsv(xVal) };
                foreach (var series in Series)
                {
                    var point = series.Points.FirstOrDefault(p =>
                        (p.Label ?? p.X.ToString(CultureInfo.InvariantCulture)) == xVal);
                    row.Add(point != null ? point.Y.ToString(CultureInfo.InvariantCulture) : "");
                }

                sb.AppendLine(string.Join(",", row));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Convert to JSON-compatible dictionary
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["type"] = Type.ToString(),
            ["title"] = Title,
            ["xLabel"] = XLabel,
            ["yLabel"] = YLabel,
            ["series"] = Series.Select(s => s.ToDictionary()).ToList(),
            ["confidence"] = Confidence,
            ["extractionMethod"] = ExtractionMethod,
            ["totalDataPoints"] = TotalDataPoints
        };
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

/// <summary>
///     A single data series within a chart (e.g., one line in a line chart, one set of bars)
/// </summary>
public class ChartDataSeries
{
    /// <summary>
    ///     Name of the series (from legend or auto-generated)
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Color of the series if detected (hex code or name)
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    ///     Data points in this series
    /// </summary>
    public List<ChartDataPoint> Points { get; init; } = [];

    /// <summary>
    ///     Convert to JSON-compatible dictionary
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["color"] = Color,
            ["points"] = Points.Select(p => p.ToDictionary()).ToList()
        };
    }
}

/// <summary>
///     A single data point within a chart series
/// </summary>
public record ChartDataPoint
{
    /// <summary>
    ///     X-axis value (numeric position)
    /// </summary>
    public double X { get; init; }

    /// <summary>
    ///     Y-axis value
    /// </summary>
    public double Y { get; init; }

    /// <summary>
    ///     Text label for the data point (e.g., category name for bar charts)
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    ///     Confidence in this specific data point extraction
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    ///     Create from numeric X and Y
    /// </summary>
    public static ChartDataPoint From(double x, double y, string? label = null)
        => new() { X = x, Y = y, Label = label };

    /// <summary>
    ///     Create from category label and Y value (for bar/pie charts)
    /// </summary>
    public static ChartDataPoint FromCategory(string label, double value, int index = 0)
        => new() { X = index, Y = value, Label = label };

    /// <summary>
    ///     Convert to JSON-compatible dictionary
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["x"] = X,
            ["y"] = Y,
            ["label"] = Label,
            ["confidence"] = Confidence
        };
    }
}

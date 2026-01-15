using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Layout;
using Mostlylucid.DocSummarizer.Images.Services.Vision;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Table Extraction Wave - Converts extracted table images to CSV using Vision LLM.
/// Runs after LayoutRoutingWave, processes table images and outputs structured CSV
/// for downstream DataSummarizer profiling.
///
/// Priority: 62 (after LayoutRoutingWave at 63)
/// </summary>
public class TableExtractionWave : IAnalysisWave
{
    private readonly VisionLlmService _visionService;
    private readonly ILogger<TableExtractionWave>? _logger;
    private readonly string _outputDirectory;

    public string Name => "TableExtractionWave";
    public int Priority => 62; // After LayoutRoutingWave (63)
    public IReadOnlyList<string> Tags => new[] { "table", "extraction", "csv", SignalTags.Content };

    private const string TableExtractionPrompt = @"You are a precise table extraction assistant. Analyze this image of a table and extract ALL data into CSV format.

RULES:
1. Output ONLY valid CSV data - no explanations, no markdown, no extra text
2. First row must be the header row with column names
3. Use comma (,) as delimiter
4. Wrap fields containing commas in double quotes
5. If a cell is empty, leave it empty between commas
6. Preserve the exact structure - same number of columns in each row
7. Include ALL rows visible in the table
8. If you cannot read a cell, use [unreadable] as placeholder

Output the CSV data now:";

    public TableExtractionWave(
        VisionLlmService visionService,
        ILogger<TableExtractionWave>? logger = null)
    {
        _visionService = visionService;
        _logger = logger;
        _outputDirectory = Path.Combine(Path.GetTempPath(), "lucidrag", "extracted_tables");
        Directory.CreateDirectory(_outputDirectory);
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Only run if we have extracted table images from LayoutRoutingWave
        return context.HasSignal("layout.tables.extracted");
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var tableImagePaths = context.GetCached<List<string>>("layout.table_image_paths");
        if (tableImagePaths == null || tableImagePaths.Count == 0)
        {
            _logger?.LogDebug("No table image paths found in context");
            return signals;
        }

        _logger?.LogInformation("Extracting {Count} tables to CSV from {Image}",
            tableImagePaths.Count, Path.GetFileName(imagePath));

        var extractedCsvPaths = new List<string>();
        var tableResults = new List<TableExtractionResult>();

        for (int i = 0; i < tableImagePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var tableImagePath = tableImagePaths[i];
            if (!File.Exists(tableImagePath))
            {
                _logger?.LogWarning("Table image not found: {Path}", tableImagePath);
                continue;
            }

            try
            {
                var result = await ExtractTableToCsvAsync(tableImagePath, i, ct);
                if (result.Success && !string.IsNullOrEmpty(result.CsvPath))
                {
                    extractedCsvPaths.Add(result.CsvPath);
                    tableResults.Add(result);

                    signals.Add(new Signal
                    {
                        Key = $"table.{i}.csv_path",
                        Value = result.CsvPath,
                        Confidence = result.Confidence,
                        Source = Name,
                        Tags = new List<string> { "table", "csv", "path" }
                    });

                    signals.Add(new Signal
                    {
                        Key = $"table.{i}.row_count",
                        Value = result.RowCount,
                        Confidence = result.Confidence,
                        Source = Name,
                        Tags = new List<string> { "table", "stats" }
                    });

                    signals.Add(new Signal
                    {
                        Key = $"table.{i}.column_count",
                        Value = result.ColumnCount,
                        Confidence = result.Confidence,
                        Source = Name,
                        Tags = new List<string> { "table", "stats" }
                    });

                    _logger?.LogInformation(
                        "Table {Index}: Extracted {Rows} rows x {Cols} columns to {Path}",
                        i, result.RowCount, result.ColumnCount, result.CsvPath);
                }
                else
                {
                    signals.Add(new Signal
                    {
                        Key = $"table.{i}.error",
                        Value = result.Error ?? "Unknown error",
                        Confidence = 0,
                        Source = Name,
                        Tags = new List<string> { "table", "error" }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to extract table {Index}", i);
                signals.Add(new Signal
                {
                    Key = $"table.{i}.error",
                    Value = ex.Message,
                    Confidence = 0,
                    Source = Name,
                    Tags = new List<string> { "table", "error" }
                });
            }
        }

        sw.Stop();

        // Cache CSV paths for DataSummarizer integration
        if (extractedCsvPaths.Count > 0)
        {
            context.SetCached("table.csv_paths", extractedCsvPaths);

            signals.Add(new Signal
            {
                Key = "table.extraction.csv_paths",
                Value = extractedCsvPaths,
                Confidence = tableResults.Average(r => r.Confidence),
                Source = Name,
                Tags = new List<string> { "table", "csv", "paths" },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = extractedCsvPaths.Count,
                    ["total_rows"] = tableResults.Sum(r => r.RowCount),
                    ["source_image"] = imagePath
                }
            });
        }

        signals.Add(new Signal
        {
            Key = "table.extraction.summary",
            Value = new
            {
                TablesProcessed = tableImagePaths.Count,
                TablesExtracted = extractedCsvPaths.Count,
                TotalRows = tableResults.Sum(r => r.RowCount),
                DurationMs = sw.ElapsedMilliseconds
            },
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "summary" }
        });

        signals.Add(new Signal
        {
            Key = "table.extraction.duration_ms",
            Value = sw.ElapsedMilliseconds,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "timing" }
        });

        _logger?.LogInformation(
            "Table extraction complete: {Extracted}/{Total} tables in {Ms}ms",
            extractedCsvPaths.Count, tableImagePaths.Count, sw.ElapsedMilliseconds);

        return signals;
    }

    /// <summary>
    /// Extract a single table image to CSV using Vision LLM.
    /// </summary>
    private async Task<TableExtractionResult> ExtractTableToCsvAsync(
        string tableImagePath,
        int index,
        CancellationToken ct)
    {
        var result = await _visionService.AnalyzeImageAsync(
            tableImagePath,
            TableExtractionPrompt,
            ct: ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Caption))
        {
            return new TableExtractionResult
            {
                Success = false,
                Error = result.Error ?? "Vision LLM returned empty response"
            };
        }

        // Parse and validate the CSV response
        var csvContent = CleanCsvResponse(result.Caption);
        if (string.IsNullOrWhiteSpace(csvContent))
        {
            return new TableExtractionResult
            {
                Success = false,
                Error = "Could not extract valid CSV from response"
            };
        }

        // Count rows and columns
        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return new TableExtractionResult
            {
                Success = false,
                Error = "No rows in extracted CSV"
            };
        }

        var columnCount = CountCsvColumns(lines[0]);
        var rowCount = lines.Length - 1; // Exclude header

        // Generate output path
        var baseName = Path.GetFileNameWithoutExtension(tableImagePath);
        var csvPath = Path.Combine(_outputDirectory, $"{baseName}_table_{index}.csv");

        // Write CSV file
        await File.WriteAllTextAsync(csvPath, csvContent, Encoding.UTF8, ct);

        // Calculate confidence based on consistency
        var confidence = CalculateExtractionConfidence(lines, columnCount);

        return new TableExtractionResult
        {
            Success = true,
            CsvPath = csvPath,
            RowCount = rowCount,
            ColumnCount = columnCount,
            Confidence = confidence
        };
    }

    /// <summary>
    /// Clean up the LLM response to extract valid CSV content.
    /// </summary>
    private static string CleanCsvResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return string.Empty;

        var cleaned = response.Trim();

        // Remove markdown code block markers
        if (cleaned.StartsWith("```"))
        {
            var endOfFirstLine = cleaned.IndexOf('\n');
            if (endOfFirstLine > 0)
                cleaned = cleaned[(endOfFirstLine + 1)..];
        }
        if (cleaned.EndsWith("```"))
        {
            cleaned = cleaned[..^3];
        }

        // Remove "csv" language identifier if present
        if (cleaned.StartsWith("csv", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[3..].TrimStart();
        }

        // Remove any preamble text before the actual CSV
        var lines = cleaned.Split('\n');
        var csvStartIndex = 0;
        for (int i = 0; i < Math.Min(5, lines.Length); i++)
        {
            var line = lines[i].Trim();
            // Look for first line that looks like CSV (contains comma and no colon at start)
            if (line.Contains(',') && !line.StartsWith("Note:") && !line.StartsWith("Here"))
            {
                csvStartIndex = i;
                break;
            }
        }

        if (csvStartIndex > 0)
        {
            cleaned = string.Join('\n', lines.Skip(csvStartIndex));
        }

        return cleaned.Trim();
    }

    /// <summary>
    /// Count CSV columns handling quoted fields properly.
    /// </summary>
    private static int CountCsvColumns(string headerLine)
    {
        var count = 0;
        var inQuotes = false;

        foreach (var c in headerLine)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
                count++;
        }

        return count + 1; // Number of commas + 1 = number of columns
    }

    /// <summary>
    /// Calculate extraction confidence based on CSV consistency.
    /// </summary>
    private static double CalculateExtractionConfidence(string[] lines, int expectedColumns)
    {
        if (lines.Length <= 1)
            return 0.5;

        var consistentRows = 0;
        var hasUnreadable = false;

        foreach (var line in lines)
        {
            var cols = CountCsvColumns(line);
            if (cols == expectedColumns)
                consistentRows++;

            if (line.Contains("[unreadable]"))
                hasUnreadable = true;
        }

        var consistency = (double)consistentRows / lines.Length;
        var confidence = consistency * 0.9; // Base confidence from consistency

        if (hasUnreadable)
            confidence *= 0.8; // Penalty for unreadable cells

        return Math.Clamp(confidence, 0.1, 0.95);
    }
}

/// <summary>
/// Result of a table extraction operation.
/// </summary>
public record TableExtractionResult
{
    public bool Success { get; init; }
    public string? CsvPath { get; init; }
    public string? Error { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public double Confidence { get; init; }
}

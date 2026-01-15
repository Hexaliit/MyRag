using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Services;
using Mostlylucid.DocSummarizer.Data.Services.Analysis;
using Mostlylucid.DocSummarizer.Data.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Table Profiling Wave - Profiles extracted CSV tables using DataSummarizer.
/// Runs after TableExtractionWave, processes CSV files and emits data quality signals.
///
/// Priority: 61 (after TableExtractionWave at 62)
/// </summary>
public class TableProfilingWave : IAnalysisWave
{
    private readonly IDataProcessor? _dataProcessor;
    private readonly DataAnalysisOrchestrator? _dataOrchestrator;
    private readonly ILogger<TableProfilingWave>? _logger;

    public string Name => "TableProfilingWave";
    public int Priority => 61; // After TableExtractionWave (62)
    public IReadOnlyList<string> Tags => new[] { "table", "profiling", "data", SignalTags.Content };

    public TableProfilingWave(
        IDataProcessor? dataProcessor = null,
        DataAnalysisOrchestrator? dataOrchestrator = null,
        ILogger<TableProfilingWave>? logger = null)
    {
        _dataProcessor = dataProcessor;
        _dataOrchestrator = dataOrchestrator;
        _logger = logger;
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Only run if we have extracted CSV paths and DataSummarizer is available
        if (_dataProcessor == null)
        {
            _logger?.LogDebug("DataSummarizer not available, skipping table profiling");
            return false;
        }

        return context.HasSignal("table.extraction.csv_paths");
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var csvPaths = context.GetCached<List<string>>("table.csv_paths");
        if (csvPaths == null || csvPaths.Count == 0)
        {
            _logger?.LogDebug("No CSV paths found in context");
            return signals;
        }

        _logger?.LogInformation("Profiling {Count} extracted tables with DataSummarizer",
            csvPaths.Count);

        var profileResults = new List<TableProfileResult>();

        for (int i = 0; i < csvPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var csvPath = csvPaths[i];
            if (!File.Exists(csvPath))
            {
                _logger?.LogWarning("CSV file not found: {Path}", csvPath);
                continue;
            }

            try
            {
                var result = await ProfileTableAsync(csvPath, i, ct);
                if (result != null)
                {
                    profileResults.Add(result);
                    EmitTableSignals(signals, result, i);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to profile table {Index}: {Path}", i, csvPath);
                signals.Add(new Signal
                {
                    Key = $"table.profile.{i}.error",
                    Value = ex.Message,
                    Confidence = 0,
                    Source = Name,
                    Tags = new List<string> { "table", "profile", "error" }
                });
            }
        }

        sw.Stop();

        // Emit profiling summary
        if (profileResults.Count > 0)
        {
            signals.Add(new Signal
            {
                Key = "table.profiling.summary",
                Value = new
                {
                    TablesProfiled = profileResults.Count,
                    TotalRows = profileResults.Sum(r => r.RowCount),
                    TotalColumns = profileResults.Sum(r => r.ColumnCount),
                    AvgCompleteness = profileResults.Average(r => r.Completeness),
                    DurationMs = sw.ElapsedMilliseconds
                },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "table", "profiling", "summary" }
            });

            // Cache profiles for downstream use
            context.SetCached("table.profiles", profileResults);
        }

        signals.Add(new Signal
        {
            Key = "table.profiling.duration_ms",
            Value = sw.ElapsedMilliseconds,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "profiling", "timing" }
        });

        _logger?.LogInformation(
            "Table profiling complete: {Count} tables in {Ms}ms",
            profileResults.Count, sw.ElapsedMilliseconds);

        return signals;
    }

    /// <summary>
    /// Profile a single CSV table using DataSummarizer.
    /// </summary>
    private async Task<TableProfileResult?> ProfileTableAsync(
        string csvPath,
        int index,
        CancellationToken ct)
    {
        if (_dataProcessor == null)
            return null;

        // Get schema first
        DataSchema? schema = null;
        try
        {
            schema = await _dataProcessor.GetSchemaAsync(csvPath, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get schema for {Path}", csvPath);
        }

        // Process the CSV
        var result = await _dataProcessor.ProcessAsync(csvPath, ct);
        if (!result.Success)
        {
            _logger?.LogWarning("DataProcessor failed for {Path}: {Error}", csvPath, result.Error);
            return null;
        }

        // Run full analysis if orchestrator is available
        DynamicDataProfile? profile = null;
        if (_dataOrchestrator != null)
        {
            try
            {
                var dataFile = new DataFile(csvPath);
                profile = await _dataOrchestrator.AnalyzeAsync(dataFile, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DataAnalysisOrchestrator failed for {Path}", csvPath);
            }
        }

        // Calculate completeness from profile or estimate
        var completeness = 1.0;
        if (profile != null)
        {
            var qualitySignals = profile.GetSignalsByTag("quality").ToList();
            if (qualitySignals.Count > 0)
            {
                var nullRates = qualitySignals
                    .Where(s => s.Key.Contains("null_rate"))
                    .Select(s => Convert.ToDouble(s.Value ?? 0))
                    .ToList();
                if (nullRates.Count > 0)
                {
                    completeness = 1.0 - nullRates.Average();
                }
            }
        }

        return new TableProfileResult
        {
            CsvPath = csvPath,
            TableIndex = index,
            RowCount = result.RowCount,
            ColumnCount = result.ColumnCount,
            ColumnNames = result.ColumnNames,
            Completeness = completeness,
            Profile = profile,
            ProcessingResult = result
        };
    }

    /// <summary>
    /// Emit signals for a profiled table.
    /// </summary>
    private void EmitTableSignals(List<Signal> signals, TableProfileResult result, int index)
    {
        // Basic table info
        signals.Add(new Signal
        {
            Key = $"table.profile.{index}.row_count",
            Value = result.RowCount,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "profile", "stats" }
        });

        signals.Add(new Signal
        {
            Key = $"table.profile.{index}.column_count",
            Value = result.ColumnCount,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "profile", "stats" }
        });

        signals.Add(new Signal
        {
            Key = $"table.profile.{index}.columns",
            Value = result.ColumnNames,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "profile", "schema" }
        });

        signals.Add(new Signal
        {
            Key = $"table.profile.{index}.completeness",
            Value = result.Completeness,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "table", "profile", "quality" }
        });

        // If we have full profile, emit key insights
        if (result.Profile != null)
        {
            // Data domain classification
            var domainSignal = result.Profile.GetBestSignal("characterization.domain");
            if (domainSignal?.Value != null)
            {
                signals.Add(new Signal
                {
                    Key = $"table.profile.{index}.domain",
                    Value = domainSignal.Value,
                    Confidence = domainSignal.Confidence,
                    Source = Name,
                    Tags = new List<string> { "table", "profile", "domain" }
                });
            }

            // PII detection
            var piiSignals = result.Profile.GetSignalsByTag("pii").ToList();
            if (piiSignals.Count > 0)
            {
                signals.Add(new Signal
                {
                    Key = $"table.profile.{index}.has_pii",
                    Value = true,
                    Confidence = piiSignals.Max(s => s.Confidence),
                    Source = Name,
                    Tags = new List<string> { "table", "profile", "pii", "security" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["pii_columns"] = piiSignals
                            .Select(s => s.Key.Replace("pii.", "").Replace(".type", ""))
                            .Distinct()
                            .ToList()
                    }
                });
            }

            // Outlier detection
            var outlierSignals = result.Profile.GetSignalsByTag("outlier").ToList();
            if (outlierSignals.Count > 0)
            {
                var totalOutliers = outlierSignals
                    .Where(s => s.Key.Contains(".count"))
                    .Sum(s => Convert.ToInt32(s.Value ?? 0));
                if (totalOutliers > 0)
                {
                    signals.Add(new Signal
                    {
                        Key = $"table.profile.{index}.outlier_count",
                        Value = totalOutliers,
                        Confidence = 0.9,
                        Source = Name,
                        Tags = new List<string> { "table", "profile", "quality" }
                    });
                }
            }

            // Summary text for RAG
            var summary = result.Profile.ToSummaryText();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                signals.Add(new Signal
                {
                    Key = $"table.profile.{index}.summary",
                    Value = summary,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "table", "profile", "text", SignalTags.Content }
                });
            }
        }

        _logger?.LogInformation(
            "Table {Index}: {Rows} rows x {Cols} columns, {Completeness:P1} complete",
            index, result.RowCount, result.ColumnCount, result.Completeness);
    }
}

/// <summary>
/// Result of profiling a single table.
/// </summary>
public record TableProfileResult
{
    public required string CsvPath { get; init; }
    public int TableIndex { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public List<string> ColumnNames { get; init; } = new();
    public double Completeness { get; init; }
    public DynamicDataProfile? Profile { get; init; }
    public DataProcessingResult? ProcessingResult { get; init; }
}

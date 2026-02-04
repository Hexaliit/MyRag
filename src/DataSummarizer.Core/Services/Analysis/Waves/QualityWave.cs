using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
///     Fifth wave: assesses data quality - null rates, completeness, duplicates.
/// </summary>
public class QualityWave : IDataAnalysisWave
{
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<QualityWave>? _logger;

    public QualityWave(DuckDbAnalyzer analyzer, ILogger<QualityWave>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public string Name => "QualityWave";
    public int Priority => 80;
    public IReadOnlyList<string> Tags => [DataSignalTags.Quality];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        return context.GetColumnNames().Count > 0 && file.Format != DataFormat.Excel;
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var columns = context.GetColumnNames();
        var rowCount = context.GetRowCount();

        if (rowCount == 0)
        {
            _logger?.LogWarning("QualityWave: No rows to analyze");
            return signals;
        }

        // Analyze columns in parallel
        var columnTasks = columns.Select(async column =>
        {
            var colSignals = new List<DataSignal>();

            try
            {
                // Get null count
                var nullCount = await _analyzer.GetNullCountAsync(column, ct);
                var nullRate = (double)nullCount / rowCount;
                var completeness = 1.0 - nullRate;

                colSignals.Add(CreateSignal(
                    DataSignalKeys.QualityNullRate(column),
                    nullRate,
                    new Dictionary<string, object> { ["null_count"] = nullCount }));

                colSignals.Add(CreateSignal(
                    DataSignalKeys.QualityCompleteness(column),
                    completeness));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "QualityWave: Failed to analyze quality for column {Column}", column);
            }

            return colSignals;
        });

        var columnResults = await Task.WhenAll(columnTasks);
        foreach (var colSignals in columnResults) signals.AddRange(colSignals);

        // Check for duplicate rows (separate task)
        try
        {
            var duplicateCount = await _analyzer.GetDuplicateCountAsync(ct);
            signals.Add(CreateSignal(DataSignalKeys.DuplicateRows, duplicateCount));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "QualityWave: Failed to check for duplicates");
        }

        _logger?.LogDebug("QualityWave: Analyzed quality for {Count} columns", columns.Count);

        return signals;
    }

    private DataSignal CreateSignal(string key, object? value, Dictionary<string, object>? metadata = null)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = 1.0,
            Tags = [DataSignalTags.Quality],
            Metadata = metadata
        };
    }
}
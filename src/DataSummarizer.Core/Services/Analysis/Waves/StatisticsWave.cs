using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
///     Sixth wave: computes statistics for numeric columns.
/// </summary>
public class StatisticsWave : IDataAnalysisWave
{
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<StatisticsWave>? _logger;

    public StatisticsWave(DuckDbAnalyzer analyzer, ILogger<StatisticsWave>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public string Name => "StatisticsWave";
    public int Priority => 70;
    public IReadOnlyList<string> Tags => [DataSignalTags.Statistics];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Only run if we have numeric columns
        return context.GetColumnsOfType("numeric").Any() && file.Format != DataFormat.Excel;
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var numericColumns = context.GetColumnsOfType("numeric").ToList();

        if (numericColumns.Count == 0)
        {
            _logger?.LogDebug("StatisticsWave: No numeric columns to analyze");
            return signals;
        }

        // Process numeric columns in parallel
        var tasks = numericColumns.Select(async column =>
        {
            var colSignals = new List<DataSignal>();

            try
            {
                // Get basic stats
                var stats = await _analyzer.GetNumericStatsAsync(column, ct);
                if (stats.HasValue)
                {
                    var (min, max, mean, stddev) = stats.Value;

                    colSignals.Add(CreateSignal(DataSignalKeys.StatsMin(column), min));
                    colSignals.Add(CreateSignal(DataSignalKeys.StatsMax(column), max));
                    colSignals.Add(CreateSignal(DataSignalKeys.StatsMean(column), mean));
                    colSignals.Add(CreateSignal(DataSignalKeys.StatsStdDev(column), stddev));
                }

                // Get quartiles
                var quartiles = await _analyzer.GetQuartilesAsync(column, ct);
                if (quartiles.HasValue)
                {
                    var (q1, median, q3) = quartiles.Value;
                    colSignals.Add(CreateSignal(
                        DataSignalKeys.StatsQuartiles(column),
                        new[] { q1, median, q3 }));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "StatisticsWave: Failed to compute stats for column {Column}", column);
            }

            return colSignals;
        });

        var results = await Task.WhenAll(tasks);
        foreach (var colSignals in results) signals.AddRange(colSignals);

        _logger?.LogDebug("StatisticsWave: Computed statistics for {Count} numeric columns", numericColumns.Count);

        return signals;
    }

    private DataSignal CreateSignal(string key, object? value)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = 1.0,
            Tags = [DataSignalTags.Statistics]
        };
    }
}
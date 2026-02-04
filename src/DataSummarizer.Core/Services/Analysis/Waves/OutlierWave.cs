using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
///     Seventh wave: detects outliers using IQR method.
/// </summary>
public class OutlierWave : IDataAnalysisWave
{
    // IQR multiplier for outlier detection (typically 1.5)
    private const double IqrMultiplier = 1.5;
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<OutlierWave>? _logger;

    public OutlierWave(DuckDbAnalyzer analyzer, ILogger<OutlierWave>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public string Name => "OutlierWave";
    public int Priority => 60;
    public IReadOnlyList<string> Tags => [DataSignalTags.Outlier];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Only run if enabled and we have numeric columns with quartiles
        return context.IsFeatureEnabled("outlier") &&
               context.GetColumnsOfType("numeric").Any() &&
               file.Format != DataFormat.Excel;
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var numericColumns = context.GetColumnsOfType("numeric").ToList();

        // Process columns in parallel
        var tasks = numericColumns.Select(async column =>
        {
            var colSignals = new List<DataSignal>();

            try
            {
                // Get quartiles from previous wave
                var quartilesSignal = context.GetBestSignal(DataSignalKeys.StatsQuartiles(column));
                if (quartilesSignal?.Value is not double[] quartiles || quartiles.Length < 3) return colSignals;

                var q1 = quartiles[0];
                var q3 = quartiles[2];
                var iqr = q3 - q1;

                // Calculate bounds
                var lowerBound = q1 - IqrMultiplier * iqr;
                var upperBound = q3 + IqrMultiplier * iqr;

                // Count outliers
                var outlierCount = await CountOutliersAsync(column, lowerBound, upperBound, ct);

                colSignals.Add(CreateSignal(DataSignalKeys.OutlierCount(column), outlierCount));
                colSignals.Add(CreateSignal(
                    DataSignalKeys.OutlierIqrBounds(column),
                    new[] { lowerBound, upperBound },
                    new Dictionary<string, object>
                    {
                        ["q1"] = q1,
                        ["q3"] = q3,
                        ["iqr"] = iqr,
                        ["multiplier"] = IqrMultiplier
                    }));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OutlierWave: Failed to detect outliers for column {Column}", column);
            }

            return colSignals;
        });

        var results = await Task.WhenAll(tasks);
        foreach (var colSignals in results) signals.AddRange(colSignals);

        _logger?.LogDebug("OutlierWave: Analyzed {Count} numeric columns for outliers", numericColumns.Count);

        return signals;
    }

    private async Task<int> CountOutliersAsync(string column, double lowerBound, double upperBound,
        CancellationToken ct)
    {
        var sql = $"""
                   SELECT COUNT(*) FROM data
                   WHERE "{column}" IS NOT NULL
                   AND ("{column}"::DOUBLE < {lowerBound} OR "{column}"::DOUBLE > {upperBound})
                   """;

        return await _analyzer.QueryScalarAsync<int>(sql, ct);
    }

    private DataSignal CreateSignal(string key, object? value, Dictionary<string, object>? metadata = null)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = 1.0,
            Tags = [DataSignalTags.Outlier],
            Metadata = metadata
        };
    }
}
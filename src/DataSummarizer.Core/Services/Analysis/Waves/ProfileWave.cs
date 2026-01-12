using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
/// Fourth wave: profiles each column for cardinality, distribution, and top values.
/// </summary>
public class ProfileWave : IDataAnalysisWave
{
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<ProfileWave>? _logger;

    public ProfileWave(DuckDbAnalyzer analyzer, ILogger<ProfileWave>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public string Name => "ProfileWave";
    public int Priority => 90;
    public IReadOnlyList<string> Tags => [DataSignalTags.Profile];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Requires columns from identity wave
        return context.GetColumnNames().Count > 0 && file.Format != DataFormat.Excel;
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var columns = context.GetColumnNames();

        // Process columns in parallel for efficiency
        var tasks = columns.Select(async column =>
        {
            var colSignals = new List<DataSignal>();

            try
            {
                // Get cardinality
                var cardinality = await _analyzer.GetCardinalityAsync(column, ct);
                colSignals.Add(CreateSignal(DataSignalKeys.ProfileCardinality(column), (int)cardinality));

                // Get top values (for categorical/low-cardinality columns)
                if (cardinality <= 100)
                {
                    var topValues = await _analyzer.GetTopValuesAsync(column, 10, ct);
                    colSignals.Add(CreateSignal(DataSignalKeys.ProfileTopValues(column), topValues));
                }

                // Detect distribution type
                var distribution = DetectDistribution(cardinality, context.GetRowCount());
                colSignals.Add(CreateSignal(DataSignalKeys.ProfileDistribution(column), distribution));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ProfileWave: Failed to profile column {Column}", column);
            }

            return colSignals;
        });

        var results = await Task.WhenAll(tasks);
        foreach (var colSignals in results)
        {
            signals.AddRange(colSignals);
        }

        _logger?.LogDebug("ProfileWave: Profiled {Count} columns", columns.Count);

        return signals;
    }

    private static string DetectDistribution(long cardinality, long rowCount)
    {
        if (rowCount == 0) return "empty";

        var uniqueRatio = (double)cardinality / rowCount;

        return uniqueRatio switch
        {
            >= 0.99 => "unique", // Almost all unique (ID-like)
            >= 0.5 => "high_cardinality", // Many distinct values
            >= 0.1 => "medium_cardinality",
            >= 0.01 => "low_cardinality",
            _ => "sparse" // Very few distinct values
        };
    }

    private DataSignal CreateSignal(string key, object? value)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = 1.0,
            Tags = [DataSignalTags.Profile]
        };
    }
}

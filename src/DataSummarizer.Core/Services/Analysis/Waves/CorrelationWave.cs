using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
/// Eighth wave: computes correlations between numeric columns.
/// Expensive for many columns - disabled by default.
/// </summary>
public class CorrelationWave : IDataAnalysisWave
{
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<CorrelationWave>? _logger;

    // Skip correlation if more than this many numeric columns (n^2 complexity)
    private const int MaxColumnsForCorrelation = 20;

    // Only report correlations above this threshold
    private const double SignificantCorrelationThreshold = 0.5;

    public CorrelationWave(DuckDbAnalyzer analyzer, ILogger<CorrelationWave>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public string Name => "CorrelationWave";
    public int Priority => 50;
    public IReadOnlyList<string> Tags => [DataSignalTags.Correlation];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Only run if enabled, have numeric columns, and not too many
        var numericColumns = context.GetColumnsOfType("numeric").ToList();
        return context.IsFeatureEnabled("correlation") &&
               numericColumns.Count >= 2 &&
               numericColumns.Count <= MaxColumnsForCorrelation &&
               file.Format != DataFormat.Excel;
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var numericColumns = context.GetColumnsOfType("numeric").ToList();

        // Generate pairs
        var pairs = new List<(string col1, string col2)>();
        for (int i = 0; i < numericColumns.Count; i++)
        {
            for (int j = i + 1; j < numericColumns.Count; j++)
            {
                pairs.Add((numericColumns[i], numericColumns[j]));
            }
        }

        _logger?.LogDebug("CorrelationWave: Computing {Count} correlations", pairs.Count);

        // Compute correlations in parallel
        var tasks = pairs.Select(async pair =>
        {
            try
            {
                var correlation = await _analyzer.GetCorrelationAsync(pair.col1, pair.col2, ct);

                if (correlation.HasValue && Math.Abs(correlation.Value) >= SignificantCorrelationThreshold)
                {
                    return CreateSignal(
                        DataSignalKeys.Correlation(pair.col1, pair.col2),
                        correlation.Value,
                        confidence: Math.Abs(correlation.Value));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "CorrelationWave: Failed to compute correlation for {Col1} and {Col2}",
                    pair.col1, pair.col2);
            }

            return null;
        });

        var results = await Task.WhenAll(tasks);
        signals.AddRange(results.Where(s => s != null)!);

        _logger?.LogDebug("CorrelationWave: Found {Count} significant correlations", signals.Count);

        return signals;
    }

    private DataSignal CreateSignal(string key, object? value, double confidence)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = confidence,
            Tags = [DataSignalTags.Correlation]
        };
    }
}

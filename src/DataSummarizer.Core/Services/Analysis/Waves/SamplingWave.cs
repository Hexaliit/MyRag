using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Data.Config;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
///     Second wave: samples rows for subsequent analysis waves.
///     Uses reservoir sampling for memory efficiency on large files.
/// </summary>
public class SamplingWave : IDataAnalysisWave
{
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<SamplingWave>? _logger;
    private readonly DataProcessorOptions _options;

    public SamplingWave(
        DuckDbAnalyzer analyzer,
        IOptions<DataProcessorOptions> options,
        ILogger<SamplingWave>? logger = null)
    {
        _analyzer = analyzer;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "SamplingWave";
    public int Priority => 100;
    public IReadOnlyList<string> Tags => [DataSignalTags.Sample];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Skip if Excel - sampling handled differently
        return file.Format != DataFormat.Excel;
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();

        var rowCount = context.GetRowCount();
        var sampleSize = _options.SampleSize;

        // If SampleSize is 0 or row count is less than sample size, sample all
        if (sampleSize <= 0 || rowCount <= sampleSize) sampleSize = (int)Math.Min(rowCount, int.MaxValue);

        try
        {
            // Use DuckDB's SAMPLE clause for efficient sampling
            var samples = await _analyzer.SampleRowsAsync(sampleSize, ct);

            // Cache the samples for subsequent waves
            context.SetCached("sample.rows", samples);

            signals.Add(new DataSignal
            {
                Key = DataSignalKeys.SampleSize,
                Value = samples.Count,
                Source = Name,
                Confidence = 1.0,
                Tags = [DataSignalTags.Sample],
                Metadata = new Dictionary<string, object>
                {
                    ["total_rows"] = rowCount,
                    ["sample_rate"] = rowCount > 0 ? (double)samples.Count / rowCount : 1.0
                }
            });

            _logger?.LogDebug("SamplingWave: Sampled {SampleSize} rows from {TotalRows} total",
                samples.Count, rowCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SamplingWave: Failed to sample {FileName}", file.FileName);

            signals.Add(new DataSignal
            {
                Key = "error.sampling",
                Value = ex.Message,
                Source = Name,
                Confidence = 1.0,
                Tags = [DataSignalTags.Sample, "error"]
            });
        }

        return signals;
    }
}
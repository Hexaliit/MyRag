using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Data.Config;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis;

/// <summary>
///     Orchestrates data analysis waves in priority order.
/// </summary>
public class DataAnalysisOrchestrator
{
    private readonly ILogger<DataAnalysisOrchestrator> _logger;
    private readonly DataProcessorOptions _options;
    private readonly IEnumerable<IDataAnalysisWave> _waves;

    public DataAnalysisOrchestrator(
        IEnumerable<IDataAnalysisWave> waves,
        IOptions<DataProcessorOptions> options,
        ILogger<DataAnalysisOrchestrator> logger)
    {
        _waves = waves.OrderByDescending(w => w.Priority).ToList();
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Analyze a data file by running all enabled waves in priority order.
    /// </summary>
    public async Task<DynamicDataProfile> AnalyzeAsync(DataFile file, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var profile = new DynamicDataProfile
        {
            SourcePath = file.FilePath
        };
        var context = new DataAnalysisContext();

        // Set feature flags from options
        context.SetFeatureEnabled("pii", _options.EnablePiiDetection);
        context.SetFeatureEnabled("outlier", _options.EnableOutlierDetection);
        context.SetFeatureEnabled("correlation", _options.EnableCorrelation);

        _logger.LogInformation("Starting data analysis for {FileName} with {WaveCount} waves",
            file.FileName, _waves.Count());

        foreach (var wave in _waves)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!wave.ShouldRun(file, context))
                {
                    _logger.LogDebug("Skipping wave {WaveName} (precondition not met)", wave.Name);
                    continue;
                }

                _logger.LogDebug("Running wave {WaveName} (priority {Priority})", wave.Name, wave.Priority);
                var waveStart = Stopwatch.StartNew();

                var signals = await wave.AnalyzeAsync(file, context, ct);
                var signalList = signals.ToList();

                // Add signals to both context (for subsequent waves) and profile (for output)
                context.AddSignals(signalList);
                profile.AddSignals(signalList);

                waveStart.Stop();
                _logger.LogDebug("Wave {WaveName} produced {SignalCount} signals in {ElapsedMs}ms",
                    wave.Name, signalList.Count, waveStart.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wave {WaveName} failed, continuing with next wave", wave.Name);

                // Add error signal
                profile.AddSignal(new DataSignal
                {
                    Key = $"error.{wave.Name}",
                    Value = ex.Message,
                    Source = wave.Name,
                    Confidence = 1.0,
                    Tags = ["error"]
                });
            }
        }

        stopwatch.Stop();
        profile.ProfileTime = stopwatch.Elapsed;

        _logger.LogInformation("Data analysis complete for {FileName}: {SignalCount} signals in {ElapsedMs}ms",
            file.FileName, profile.GetAllSignals().Count(), stopwatch.ElapsedMilliseconds);

        // Clear cache to free memory
        context.ClearCache();

        return profile;
    }

    /// <summary>
    ///     Get list of registered wave names.
    /// </summary>
    public IEnumerable<string> GetWaveNames()
    {
        return _waves.Select(w => w.Name);
    }

    /// <summary>
    ///     Get waves by tag.
    /// </summary>
    public IEnumerable<IDataAnalysisWave> GetWavesByTag(string tag)
    {
        return _waves.Where(w => w.Tags.Contains(tag));
    }
}
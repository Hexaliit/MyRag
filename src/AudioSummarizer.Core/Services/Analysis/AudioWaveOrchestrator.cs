using System.Diagnostics;
using AudioSummarizer.Core.Models;

namespace AudioSummarizer.Core.Services.Analysis;

/// <summary>
/// Orchestrates the execution of audio analysis waves in priority order.
/// Follows the wave-based architecture pattern from ImageSummarizer.Core.
/// </summary>
public sealed class AudioWaveOrchestrator
{
    private readonly IEnumerable<IAudioWave> _waves;
    private readonly ILogger<AudioWaveOrchestrator> _logger;

    public AudioWaveOrchestrator(
        IEnumerable<IAudioWave> waves,
        ILogger<AudioWaveOrchestrator> logger)
    {
        _waves = waves;
        _logger = logger;
    }

    /// <summary>
    /// Analyze an audio file by running all waves in priority order
    /// </summary>
    /// <param name="audioPath">Path to the audio file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete audio profile with all extracted signals</returns>
    public async Task<AudioProfile> AnalyzeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException($"Audio file not found: {audioPath}");
        }

        var profile = new AudioProfile { AudioPath = audioPath };
        var context = new AnalysisContext();
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Starting audio analysis: {AudioPath}", audioPath);

        // Sort waves by priority (highest first)
        var orderedWaves = _waves.OrderByDescending(w => w.Priority).ToList();

        _logger.LogDebug("Executing {WaveCount} waves in priority order", orderedWaves.Count);

        foreach (var wave in orderedWaves)
        {
            try
            {
                // Check if wave should run
                if (!wave.ShouldRun(audioPath, context))
                {
                    _logger.LogDebug("Skipping wave {WaveName} (ShouldRun returned false)", wave.Name);
                    continue;
                }

                _logger.LogDebug("Executing wave: {WaveName} (priority {Priority})", wave.Name, wave.Priority);

                var waveStopwatch = Stopwatch.StartNew();

                // Execute wave
                var signals = await wave.AnalyzeAsync(audioPath, context, cancellationToken);

                waveStopwatch.Stop();

                // Add signals to context (for subsequent waves) and profile
                var signalList = signals.ToList();
                context.AddSignals(signalList);
                profile.AddSignals(signalList);

                _logger.LogInformation(
                    "Wave {WaveName} completed in {ElapsedMs}ms, extracted {SignalCount} signals",
                    wave.Name,
                    waveStopwatch.ElapsedMilliseconds,
                    signalList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wave {WaveName} failed: {Message}", wave.Name, ex.Message);
                profile.Errors.Add($"{wave.Name}: {ex.Message}");
            }
        }

        stopwatch.Stop();
        profile.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        _logger.LogInformation(
            "Audio analysis completed in {ElapsedMs}ms, total signals: {SignalCount}, errors: {ErrorCount}",
            stopwatch.ElapsedMilliseconds,
            profile.Signals.Count,
            profile.Errors.Count);

        return profile;
    }

    /// <summary>
    /// Get all registered waves ordered by priority
    /// </summary>
    public IEnumerable<IAudioWave> GetWaves() => _waves.OrderByDescending(w => w.Priority);
}

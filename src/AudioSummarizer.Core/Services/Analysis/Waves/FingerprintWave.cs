using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Fingerprinting;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Fingerprint Wave - Generates perceptual audio fingerprints for deduplication and similarity detection.
/// Priority: 90 (runs after IdentityWave but before acoustic profiling)
/// Signals:
/// - audio.fingerprint.type: Fingerprint algorithm (spectral_peaks, chromaprint)
/// - audio.fingerprint.hash: Fingerprint hash (hex string)
/// - audio.fingerprint.provider: Provider that generated the fingerprint
/// </summary>
public sealed class FingerprintWave : IAudioWave
{
    private readonly IFingerprintService _fingerprintService;
    private readonly AudioConfig _config;
    private readonly ILogger<FingerprintWave> _logger;

    public string Name => "FingerprintWave";
    public int Priority => 90;

    public FingerprintWave(
        IFingerprintService fingerprintService,
        IOptions<AudioConfig> config,
        ILogger<FingerprintWave> logger)
    {
        _fingerprintService = fingerprintService;
        _config = config.Value;
        _logger = logger;
    }

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Only run if fingerprinting is enabled in pipeline config
        return _config.Pipeline.EnableFingerprinting;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<Signal>();

        try
        {
            _logger.LogDebug("Generating {Provider} fingerprint for {AudioPath}",
                _fingerprintService.ProviderName, audioPath);

            // Generate fingerprint
            var fingerprint = await _fingerprintService.GenerateFingerprintAsync(audioPath, cancellationToken);

            // Add fingerprint signals
            signals.Add(new Signal
            {
                Name = "audio.fingerprint.type",
                Value = fingerprint.Type,
                Type = SignalType.Identity,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.fingerprint.hash",
                Value = fingerprint.Hash,
                Type = SignalType.Identity,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.fingerprint.provider",
                Value = fingerprint.Provider ?? _fingerprintService.ProviderName,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Store raw fingerprint data if available (for similarity comparison)
            if (fingerprint.RawData != null)
            {
                signals.Add(new Signal
                {
                    Name = "audio.fingerprint.raw_data_size",
                    Value = fingerprint.RawData.Length,
                    Type = SignalType.Metadata,
                    Source = Name
                });
            }

            _logger.LogInformation(
                "Generated {Type} fingerprint: {Hash} ({Provider})",
                fingerprint.Type,
                fingerprint.Hash[..16] + "...",
                fingerprint.Provider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FingerprintWave failed for {AudioPath}: {Message}", audioPath, ex.Message);

            // Don't throw - fingerprinting failure shouldn't stop other waves
            signals.Add(new Signal
            {
                Name = "audio.fingerprint.error",
                Value = ex.Message,
                Type = SignalType.Metadata,
                Source = Name
            });
        }

        return signals;
    }
}

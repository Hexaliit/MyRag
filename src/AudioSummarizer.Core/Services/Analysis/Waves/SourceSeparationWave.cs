using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.SourceSeparation;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
///     Source Separation Wave - Separates audio into stems (vocals, drums, bass, other).
///     Priority: 55 (runs after content classification, before transcription)
///     Only runs for music or mixed content.
///     Signals:
///     - source_separation.success: Whether separation succeeded
///     - source_separation.vocals_path: Path to extracted vocals WAV
///     - source_separation.drums_path: Path to extracted drums WAV
///     - source_separation.bass_path: Path to extracted bass WAV
///     - source_separation.other_path: Path to extracted other/accompaniment WAV
///     - source_separation.instrumentals_path: Path to combined instrumentals WAV
///     - source_separation.processing_time_ms: Processing time in milliseconds
/// </summary>
public sealed class SourceSeparationWave : IAudioWave
{
    private readonly AudioConfig _config;
    private readonly ILogger<SourceSeparationWave> _logger;
    private readonly ISourceSeparationService _separationService;

    public SourceSeparationWave(
        ISourceSeparationService separationService,
        IOptions<AudioConfig> config,
        ILogger<SourceSeparationWave> logger)
    {
        _separationService = separationService;
        _config = config.Value;
        _logger = logger;
    }

    public string Name => "SourceSeparationWave";
    public int Priority => 55; // After content classification (70), before transcription (60)

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Only run for music or mixed content
        if (!_separationService.IsAvailable)
        {
            _logger.LogDebug("Source separation not available (model not downloaded)");
            return false;
        }

        // Check if source separation is enabled in config
        if (!_config.EnableSourceSeparation)
        {
            _logger.LogDebug("Source separation disabled in configuration");
            return false;
        }

        // Check content type from ContentClassifierWave
        if (context.Signals.TryGetValue("audio.content_type", out var contentTypeSignal))
        {
            var contentType = contentTypeSignal.Value?.ToString()?.ToLower();
            if (contentType == "music" || contentType == "mixed")
            {
                _logger.LogDebug("Running source separation for {ContentType} content", contentType);
                return true;
            }
        }

        // Also check music likelihood
        if (context.Signals.TryGetValue("audio.music_likelihood", out var musicSignal))
            if (musicSignal.Value is double likelihood && likelihood > 0.3)
            {
                _logger.LogDebug("Running source separation (music likelihood: {Likelihood:F2})", likelihood);
                return true;
            }

        return false;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<Signal>();

        try
        {
            _logger.LogInformation("Starting source separation for {AudioPath}", Path.GetFileName(audioPath));

            // Create output directory for stems
            var outputDir = Path.Combine(
                Path.GetDirectoryName(audioPath) ?? Path.GetTempPath(),
                "stems",
                Path.GetFileNameWithoutExtension(audioPath));

            var result = await _separationService.SeparateAsync(audioPath, outputDir, cancellationToken);

            // Add result signals
            signals.Add(CreateSignal("source_separation.success", result.Success));

            if (result.Success)
            {
                // Add paths to all stems as signals (these become evidence)
                if (!string.IsNullOrEmpty(result.VocalsPath))
                {
                    signals.Add(CreateSignal("source_separation.vocals_path", result.VocalsPath));
                    signals.Add(CreateEvidenceSignal("stem.vocals", result.VocalsPath));
                }

                if (!string.IsNullOrEmpty(result.DrumsPath))
                {
                    signals.Add(CreateSignal("source_separation.drums_path", result.DrumsPath));
                    signals.Add(CreateEvidenceSignal("stem.drums", result.DrumsPath));
                }

                if (!string.IsNullOrEmpty(result.BassPath))
                {
                    signals.Add(CreateSignal("source_separation.bass_path", result.BassPath));
                    signals.Add(CreateEvidenceSignal("stem.bass", result.BassPath));
                }

                if (!string.IsNullOrEmpty(result.OtherPath))
                {
                    signals.Add(CreateSignal("source_separation.other_path", result.OtherPath));
                    signals.Add(CreateEvidenceSignal("stem.other", result.OtherPath));
                }

                if (!string.IsNullOrEmpty(result.InstrumentalsPath))
                {
                    signals.Add(CreateSignal("source_separation.instrumentals_path", result.InstrumentalsPath));
                    signals.Add(CreateEvidenceSignal("stem.instrumentals", result.InstrumentalsPath));
                }

                signals.Add(CreateSignal("source_separation.stem_count", result.Stems.Count));
                signals.Add(CreateSignal("source_separation.processing_time_ms",
                    result.ProcessingTime.TotalMilliseconds));

                _logger.LogInformation(
                    "Source separation completed: {StemCount} stems extracted in {Time:F1}s",
                    result.Stems.Count,
                    result.ProcessingTime.TotalSeconds);
            }
            else
            {
                signals.Add(CreateSignal("source_separation.error", result.Error ?? "Unknown error"));
                _logger.LogWarning("Source separation failed: {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source separation wave failed for {AudioPath}", audioPath);
            signals.Add(CreateSignal("source_separation.success", false));
            signals.Add(CreateSignal("source_separation.error", ex.Message));
        }

        return signals;
    }

    private Signal CreateSignal(string name, object value)
    {
        return new Signal
        {
            Name = name,
            Value = value,
            Type = SignalType.Metadata,
            Source = Name
        };
    }

    private Signal CreateEvidenceSignal(string name, string filePath)
    {
        // Evidence signals contain file paths that should be stored
        // Store stem type in signal name for later extraction
        return new Signal
        {
            Name = name,
            Value = filePath,
            Type = SignalType.Evidence,
            Source = Name,
            Confidence = 1.0 // High confidence for generated files
        };
    }
}
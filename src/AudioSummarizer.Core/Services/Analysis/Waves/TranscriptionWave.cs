using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Transcription;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Transcription Wave - Converts speech to text with timestamped segments.
/// Priority: 60 (runs after fingerprinting and acoustic profiling)
/// Signals:
/// - transcription.text: Full transcript text
/// - transcription.language: Detected/specified language
/// - transcription.confidence: Overall confidence score
/// - transcription.segment_count: Number of timestamped segments
/// - transcription.provider: Service that performed transcription
/// </summary>
public sealed class TranscriptionWave : IAudioWave
{
    private readonly IEnumerable<ITranscriptionService> _transcriptionServices;
    private readonly AudioConfig _config;
    private readonly ILogger<TranscriptionWave> _logger;

    public string Name => "TranscriptionWave";
    public int Priority => 60;

    public TranscriptionWave(
        IEnumerable<ITranscriptionService> transcriptionServices,
        IOptions<AudioConfig> config,
        ILogger<TranscriptionWave> logger)
    {
        _transcriptionServices = transcriptionServices;
        _config = config.Value;
        _logger = logger;
    }

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Only run if we have transcription services registered
        return _transcriptionServices.Any();
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<Signal>();

        try
        {
            // Select transcription service based on config
            var service = await SelectTranscriptionServiceAsync(cancellationToken);

            if (service == null)
            {
                _logger.LogWarning("No transcription service available");
                signals.Add(new Signal
                {
                    Name = "transcription.error",
                    Value = "No transcription service available",
                    Type = SignalType.Metadata,
                    Source = Name
                });
                return signals;
            }

            _logger.LogDebug("Transcribing {AudioPath} using {Provider}",
                audioPath, service.ProviderName);

            // Transcribe audio
            var transcript = await service.TranscribeAsync(
                audioPath,
                _config.Whisper.Language,
                cancellationToken);

            // Add transcript signals
            signals.Add(new Signal
            {
                Name = "transcription.text",
                Value = transcript.Text,
                Type = SignalType.Transcription,
                Confidence = transcript.Confidence,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "transcription.language",
                Value = transcript.Language ?? "unknown",
                Type = SignalType.Metadata,
                Source = Name
            });

            if (transcript.Confidence.HasValue)
            {
                signals.Add(new Signal
                {
                    Name = "transcription.confidence",
                    Value = transcript.Confidence.Value,
                    Type = SignalType.Transcription,
                    Confidence = transcript.Confidence,
                    Source = Name
                });
            }

            signals.Add(new Signal
            {
                Name = "transcription.segment_count",
                Value = transcript.Segments.Count,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "transcription.provider",
                Value = transcript.Provider ?? service.ProviderName,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "transcription.processing_time_ms",
                Value = transcript.ProcessingTimeMs,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Store full transcript with segments as JSON
            var transcriptJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                text = transcript.Text,
                language = transcript.Language,
                confidence = transcript.Confidence,
                segments = transcript.Segments.Select(s => new
                {
                    start = s.Start,
                    end = s.End,
                    text = s.Text,
                    confidence = s.Confidence
                })
            }, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            signals.Add(new Signal
            {
                Name = "transcription.full_data",
                Value = transcriptJson,
                Type = SignalType.Transcription,
                Source = Name
            });

            _logger.LogInformation(
                "Transcribed {AudioPath}: {SegmentCount} segments, {CharCount} chars, confidence: {Confidence:F3} ({Provider})",
                Path.GetFileName(audioPath),
                transcript.Segments.Count,
                transcript.Text.Length,
                transcript.Confidence ?? 0,
                service.ProviderName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TranscriptionWave failed for {AudioPath}: {Message}", audioPath, ex.Message);

            // Don't throw - transcription failure shouldn't stop other waves
            signals.Add(new Signal
            {
                Name = "transcription.error",
                Value = ex.Message,
                Type = SignalType.Metadata,
                Source = Name
            });
        }

        return signals;
    }

    private async Task<ITranscriptionService?> SelectTranscriptionServiceAsync(CancellationToken cancellationToken)
    {
        switch (_config.TranscriptionBackend)
        {
            case TranscriptionBackend.Whisper:
                var whisperService = _transcriptionServices.FirstOrDefault(s => s.ProviderName == "Whisper.NET");
                if (whisperService != null && await whisperService.IsAvailableAsync(cancellationToken))
                {
                    return whisperService;
                }
                _logger.LogWarning("Whisper.NET not available");
                return null;

            case TranscriptionBackend.Ollama:
                var ollamaService = _transcriptionServices.FirstOrDefault(s => s.ProviderName == "Ollama");
                if (ollamaService != null && await ollamaService.IsAvailableAsync(cancellationToken))
                {
                    return ollamaService;
                }
                _logger.LogWarning("Ollama not available");
                return null;

            case TranscriptionBackend.Auto:
                // Try Whisper first, fallback to Ollama
                whisperService = _transcriptionServices.FirstOrDefault(s => s.ProviderName == "Whisper.NET");
                if (whisperService != null && await whisperService.IsAvailableAsync(cancellationToken))
                {
                    _logger.LogDebug("Auto-selected Whisper.NET");
                    return whisperService;
                }

                ollamaService = _transcriptionServices.FirstOrDefault(s => s.ProviderName == "Ollama");
                if (ollamaService != null && await ollamaService.IsAvailableAsync(cancellationToken))
                {
                    _logger.LogDebug("Auto-selected Ollama (Whisper.NET unavailable)");
                    return ollamaService;
                }

                _logger.LogWarning("No transcription service available (tried Whisper.NET and Ollama)");
                return null;

            default:
                return null;
        }
    }
}

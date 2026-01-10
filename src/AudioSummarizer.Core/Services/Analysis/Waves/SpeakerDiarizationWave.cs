using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Audio;
using AudioSummarizer.Core.Services.Voice;
using System.Text.Json;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Speaker Diarization Wave - Identifies who spoke when using pure .NET clustering.
/// Priority: 50 (runs after transcription, before voice embeddings)
/// Signals:
/// - speaker.count: Number of unique speakers detected
/// - speaker.turns: JSON array of speaker turns with timestamps
/// - speaker.diarization_confidence: Overall confidence score
/// - speaker.method: Diarization method used (clustering)
/// </summary>
public sealed class SpeakerDiarizationWave : IAudioWave
{
    private readonly SpeakerDiarizationService _diarizationService;
    private readonly AudioSegmentExtractor _segmentExtractor;
    private readonly AudioConfig _config;
    private readonly ILogger<SpeakerDiarizationWave> _logger;

    public string Name => "SpeakerDiarizationWave";
    public int Priority => 50;

    public SpeakerDiarizationWave(
        SpeakerDiarizationService diarizationService,
        AudioSegmentExtractor segmentExtractor,
        IOptions<AudioConfig> config,
        ILogger<SpeakerDiarizationWave> logger)
    {
        _diarizationService = diarizationService;
        _segmentExtractor = segmentExtractor;
        _config = config.Value;
        _logger = logger;
    }

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Check if diarization is enabled
        if (!_config.EnableSpeakerDiarization)
        {
            _logger.LogDebug("Speaker diarization disabled in config");
            return false;
        }

        // Only run on speech content (skip music/silence)
        if (context.Signals.TryGetValue("audio.content_type", out var contentSignal))
        {
            var contentType = contentSignal.Value?.ToString();
            if (contentType == "music" || contentType == "silence")
            {
                _logger.LogDebug("Skipping diarization for content type: {ContentType}", contentType);
                return false;
            }
        }

        // Check minimum duration
        if (context.Signals.TryGetValue("audio.duration_seconds", out var durationSignal))
        {
            if (durationSignal.Value is double duration && duration < _config.VoiceEmbedding.MinDurationSeconds)
            {
                _logger.LogDebug("Audio too short for diarization: {Duration}s", duration);
                return false;
            }
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<Signal>();

        try
        {
            _logger.LogDebug("Running speaker diarization on {AudioPath}", audioPath);

            // Perform diarization
            var result = await _diarizationService.DiarizeAsync(audioPath, cancellationToken);

            // Emit speaker count
            signals.Add(new Signal
            {
                Name = "speaker.count",
                Value = result.SpeakerCount,
                Type = SignalType.Speaker,
                Confidence = result.SpeakerCount > 0 ? 1.0 : null,
                Source = Name
            });

            // Emit speaker classification
            signals.Add(new Signal
            {
                Name = "speaker.classification",
                Value = result.SpeakerCount switch
                {
                    0 => "no_speech",
                    1 => "single_speaker",
                    2 => "two_speakers",
                    _ => "multi_speaker"
                },
                Type = SignalType.Classification,
                Confidence = result.SpeakerCount > 0 ? 1.0 : null,
                Source = Name
            });

            // Emit diarization method
            signals.Add(new Signal
            {
                Name = "speaker.diarization_method",
                Value = "agglomerative_clustering",
                Type = SignalType.Metadata,
                Source = Name
            });

            // Serialize speaker turns to JSON
            var turnsJson = JsonSerializer.Serialize(result.Turns.Select(t => new
            {
                speaker_id = t.SpeakerId,
                start_seconds = t.StartSeconds,
                end_seconds = t.EndSeconds,
                duration_seconds = t.Duration,
                confidence = t.Confidence
            }), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            signals.Add(new Signal
            {
                Name = "speaker.turns",
                Value = turnsJson,
                Type = SignalType.Speaker,
                Source = Name
            });

            // Calculate average confidence
            var avgConfidence = result.Turns.Count > 0
                ? result.Turns.Average(t => t.Confidence)
                : 0.0;

            signals.Add(new Signal
            {
                Name = "speaker.diarization_confidence",
                Value = avgConfidence,
                Type = SignalType.Speaker,
                Confidence = avgConfidence,
                Source = Name
            });

            // Emit turn count
            signals.Add(new Signal
            {
                Name = "speaker.turn_count",
                Value = result.Turns.Count,
                Type = SignalType.Speaker,
                Source = Name
            });

            // Calculate turn statistics
            if (result.Turns.Count > 0)
            {
                var avgTurnDuration = result.Turns.Average(t => t.Duration);
                var maxTurnDuration = result.Turns.Max(t => t.Duration);
                var minTurnDuration = result.Turns.Min(t => t.Duration);

                signals.Add(new Signal
                {
                    Name = "speaker.avg_turn_duration",
                    Value = avgTurnDuration,
                    Type = SignalType.Acoustic,
                    Source = Name
                });

                signals.Add(new Signal
                {
                    Name = "speaker.max_turn_duration",
                    Value = maxTurnDuration,
                    Type = SignalType.Acoustic,
                    Source = Name
                });

                signals.Add(new Signal
                {
                    Name = "speaker.min_turn_duration",
                    Value = minTurnDuration,
                    Type = SignalType.Acoustic,
                    Source = Name
                });

                // Calculate speaker participation (percentage of speaking time per speaker)
                if (context.Signals.TryGetValue("audio.duration_seconds", out var totalDurationSignal))
                {
                    var totalDuration = (double)totalDurationSignal.Value;
                    var speakerParticipation = result.Turns
                        .GroupBy(t => t.SpeakerId)
                        .ToDictionary(
                            g => g.Key,
                            g => Math.Round((g.Sum(t => t.Duration) / totalDuration) * 100, 2)
                        );

                    signals.Add(new Signal
                    {
                        Name = "speaker.participation",
                        Value = JsonSerializer.Serialize(speakerParticipation, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        }),
                        Type = SignalType.Speaker,
                        Source = Name
                    });
                }
            }

            // Extract speaker sample audio clips (2 second clips from first turn of each speaker)
            try
            {
                _logger.LogDebug("Extracting speaker samples from {AudioPath}", audioPath);

                var speakerSamples = await _segmentExtractor.ExtractSpeakerSamplesAsync(
                    audioPath,
                    result.Turns,
                    sampleDurationSeconds: 2.0,
                    cancellationToken);

                // Emit speaker samples as signals (Base64-encoded WAV)
                foreach (var (speakerId, base64Wav) in speakerSamples)
                {
                    signals.Add(new Signal
                    {
                        Name = $"speaker.sample.{speakerId.ToLowerInvariant()}",
                        Value = base64Wav,
                        Type = SignalType.Embedding, // Using Embedding type for binary data
                        Source = Name
                    });

                    _logger.LogDebug("Added audio sample for {SpeakerId} ({SizeKB} KB)",
                        speakerId,
                        Math.Round(base64Wav.Length / 1024.0, 1));
                }

                if (speakerSamples.Count > 0)
                {
                    signals.Add(new Signal
                    {
                        Name = "speaker.samples_extracted",
                        Value = speakerSamples.Count,
                        Type = SignalType.Metadata,
                        Source = Name
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract speaker samples for {AudioPath}: {Message}",
                    audioPath, ex.Message);

                signals.Add(new Signal
                {
                    Name = "speaker.sample_extraction_error",
                    Value = ex.Message,
                    Type = SignalType.Metadata,
                    Source = Name
                });
            }

            _logger.LogInformation(
                "Diarization complete for {AudioPath}: {SpeakerCount} speakers, {TurnCount} turns",
                Path.GetFileName(audioPath),
                result.SpeakerCount,
                result.Turns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeakerDiarizationWave failed for {AudioPath}: {Message}",
                audioPath, ex.Message);

            // Don't throw - diarization failure shouldn't stop other waves
            signals.Add(new Signal
            {
                Name = "speaker.diarization_error",
                Value = ex.Message,
                Type = SignalType.Metadata,
                Source = Name
            });
        }

        return signals;
    }
}

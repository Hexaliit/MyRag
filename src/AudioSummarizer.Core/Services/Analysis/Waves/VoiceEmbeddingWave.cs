using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Voice;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Extracts voice embeddings for anonymous speaker similarity
/// Uses ECAPA-TDNN ONNX model (no PII, no speaker identification)
/// Priority 30: After transcription, before diarization
/// </summary>
public class VoiceEmbeddingWave : IAudioWave
{
    private readonly ILogger<VoiceEmbeddingWave> _logger;
    private readonly VoiceEmbeddingService _embeddingService;
    private readonly AudioConfig _config;

    public int Priority => 30;
    public string Name => "VoiceEmbeddingWave";

    public VoiceEmbeddingWave(
        ILogger<VoiceEmbeddingWave> logger,
        VoiceEmbeddingService embeddingService,
        IOptions<AudioConfig> config)
    {
        _logger = logger;
        _embeddingService = embeddingService;
        _config = config.Value;
    }

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Only run if voice embeddings enabled
        if (!_config.EnableVoiceEmbeddings)
            return false;

        // Only run on speech or mixed content (not pure music)
        if (context.Signals.TryGetValue("audio.content_type", out var contentSignal))
        {
            var contentType = contentSignal.Value?.ToString();
            if (contentType == "music" || contentType == "silence")
                return false;
        }

        // Check minimum duration
        if (context.Signals.TryGetValue("audio.duration_seconds", out var durationSignal))
        {
            if (durationSignal.Value is double duration &&
                duration < _config.VoiceEmbedding.MinDurationSeconds)
            {
                _logger.LogDebug("Audio too short for voice embedding ({Duration}s < {MinDuration}s)",
                    duration, _config.VoiceEmbedding.MinDurationSeconds);
                return false;
            }
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var signals = new List<Signal>();

        try
        {
            var embedding = await _embeddingService.ExtractEmbeddingAsync(audioPath, cancellationToken);

            signals.Add(CreateSignal("speaker.voiceprint_id", embedding.VoiceprintId, null));
            signals.Add(CreateSignal("speaker.embedding_dimension", embedding.Dimension, null));
            signals.Add(CreateSignal("speaker.embedding_model", embedding.Model, null));

            // Store embedding vector as base64 for storage
            var vectorBase64 = Convert.ToBase64String(
                embedding.Vector.SelectMany(BitConverter.GetBytes).ToArray());
            signals.Add(CreateSignal("speaker.embedding_vector", vectorBase64, null));

            sw.Stop();
            _logger.LogInformation(
                "Extracted voice embedding: voiceprint={VoiceprintId}, dimension={Dimension}, time={Time}ms",
                embedding.VoiceprintId, embedding.Dimension, sw.ElapsedMilliseconds);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Voice embedding model not found. Skipping voice embedding extraction.");
            signals.Add(CreateSignal("speaker.voiceprint_id", "model_not_found", 0.0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice embedding extraction failed for {AudioPath}", audioPath);
            signals.Add(CreateSignal("speaker.voiceprint_id", "error", 0.0));
        }

        return signals;
    }

    private Signal CreateSignal(string name, object value, double? confidence)
    {
        var type = name switch
        {
            "speaker.voiceprint_id" or "speaker.embedding_model" => SignalType.Identity,
            "speaker.embedding_dimension" => SignalType.Metadata,
            "speaker.embedding_vector" => SignalType.Embedding,
            _ => SignalType.Acoustic
        };

        return new Signal
        {
            Name = name,
            Value = value,
            Confidence = confidence,
            Type = type,
            Source = Name
        };
    }
}

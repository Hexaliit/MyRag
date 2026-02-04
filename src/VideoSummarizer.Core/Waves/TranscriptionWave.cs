using System.Diagnostics;
using System.Text.Json;
using AudioSummarizer.Core.Services.Analysis;
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Stage 3: Speech transcription by chaining to AudioSummarizer.
///     Uses AudioSummarizer's wave orchestrator for Whisper transcription,
///     speaker diarization, and audio fingerprinting.
///     Falls back gracefully if subtitles already provide sufficient coverage.
/// </summary>
public class TranscriptionWave : IVideoWave
{
    private readonly AudioWaveOrchestrator? _audioOrchestrator;
    private readonly ILogger<TranscriptionWave> _logger;

    public TranscriptionWave(
        ILogger<TranscriptionWave> logger,
        AudioWaveOrchestrator? audioOrchestrator = null)
    {
        _audioOrchestrator = audioOrchestrator;
        _logger = logger;
    }

    public string Name => "transcription";
    public int Priority => 500; // After chapter extraction
    public IReadOnlyList<string> Tags => [VideoSignalTags.Speech, VideoSignalTags.Audio];

    public bool ShouldRun(VideoContext context)
    {
        // Skip if no audio was extracted
        if (string.IsNullOrEmpty(context.AudioPath))
        {
            _logger.LogInformation("No audio track - skipping transcription");
            return false;
        }

        // Skip if no AudioSummarizer available
        if (_audioOrchestrator == null)
        {
            _logger.LogWarning("AudioSummarizer not available - skipping transcription");
            return false;
        }

        // Skip if we already have enough utterances from subtitles
        // (subtitles are usually more accurate than ASR)
        if (context.Utterances.Count > 10)
        {
            var totalWords = context.Utterances.Sum(u =>
                u.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

            if (totalWords > 100)
            {
                _logger.LogInformation(
                    "Using existing {Count} utterances from subtitles ({Words} words)",
                    context.Utterances.Count, totalWords);
                return false;
            }
        }

        return true;
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        context.ReportProgress("Transcribing audio", 0);

        // Emit wave started signal
        context.AddSignal(new VideoSignal
        {
            Key = $"{Name}.started",
            Value = DateTimeOffset.UtcNow,
            Source = Name,
            Tags = [VideoSignalTags.Speech, "wave", "flow"]
        });

        var audioPath = context.AudioPath!;

        // Chain to AudioSummarizer for full audio analysis
        context.ReportProgress("Running AudioSummarizer", 10);

        _logger.LogInformation("Chaining to AudioSummarizer for audio analysis: {AudioPath}", audioPath);

        var audioProfile = await _audioOrchestrator!.AnalyzeAsync(audioPath, ct);

        context.ReportProgress("Processing audio signals", 70);

        // Store the full audio profile for downstream use
        context.SetCached("audio_profile", audioProfile);

        // Extract transcription signals
        var transcriptText = audioProfile.GetValue<string>("transcription.text");
        var detectedLanguage = audioProfile.GetValue<string>("transcription.language");
        var transcriptConfidence = audioProfile.GetValue<double?>("transcription.confidence") ?? 0.7;

        // AudioSummarizer stores segments as JSON in "transcription.full_data"
        var transcriptFullData = audioProfile.GetValue<string>("transcription.full_data");
        var transcriptSegments = ParseTranscriptSegments(transcriptFullData, detectedLanguage);

        if (!string.IsNullOrEmpty(transcriptText))
        {
            context.AddSignal(new VideoSignal
            {
                Key = "transcription.transcript",
                Value = transcriptText,
                Source = Name,
                Confidence = transcriptConfidence,
                Tags = [VideoSignalTags.Speech]
            });

            context.SetCached("audio_transcript", transcriptText);
        }

        // Convert AudioSummarizer segments to VideoSummarizer Utterances
        if (transcriptSegments.Count > 0)
        {
            _logger.LogInformation("Processing {Count} transcript segments into utterances", transcriptSegments.Count);

            foreach (var segment in transcriptSegments)
            {
                var text = segment.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var utterance = new Utterance
                {
                    Id = Guid.NewGuid(),
                    VideoId = context.Metadata!.Id,
                    Text = text,
                    StartTime = segment.StartTime,
                    EndTime = segment.EndTime,
                    Confidence = segment.Confidence,
                    Language = segment.Language ?? detectedLanguage
                };

                // Associate with shots
                utterance.ShotIds.AddRange(
                    context.Shots
                        .Where(s => s.StartTime < utterance.EndTime && s.EndTime > utterance.StartTime)
                        .Select(s => s.Id));

                // Associate with scenes
                utterance.SceneIds.AddRange(
                    context.Scenes
                        .Where(s => s.StartTime < utterance.EndTime && s.EndTime > utterance.StartTime)
                        .Select(s => s.Id));

                context.Utterances.Add(utterance);
            }
        }
        else
        {
            _logger.LogDebug("No transcript segments found in audio profile");
        }

        // Extract speaker diarization signals
        var speakers = audioProfile.GetValue<List<SpeakerInfo>>("diarization.speakers");
        var speakerSegments = audioProfile.GetValue<List<SpeakerSegment>>("diarization.segments");

        if (speakers != null)
        {
            foreach (var speaker in speakers)
                context.Speakers.Add(new Speaker
                {
                    Id = speaker.Id,
                    VideoId = context.Metadata!.Id,
                    Embedding = speaker.Embedding,
                    Name = speaker.Name,
                    TotalSpeakingTime = speaker.TotalSpeakingTime,
                    UtteranceCount = speaker.UtteranceCount,
                    Confidence = speaker.Confidence
                });

            context.AddSignal(new VideoSignal
            {
                Key = "diarization.speaker_count",
                Value = speakers.Count,
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            });
        }

        // Link utterances to speakers if diarization data is available
        if (speakerSegments != null && context.Utterances.Count > 0)
            foreach (var utterance in context.Utterances)
            {
                // Find overlapping speaker segment
                var speakerSegment = speakerSegments
                    .FirstOrDefault(s =>
                        s.StartTime < utterance.EndTime &&
                        s.EndTime > utterance.StartTime);

                if (speakerSegment != null)
                    // Update utterance's speaker ID
                    // Note: Utterance is a record, so we'd store mapping in cache
                    context.SetCached($"utterance_speaker.{utterance.Id}", speakerSegment.SpeakerId);
            }

        // Extract other audio signals
        var audioFingerprint = audioProfile.GetValue<string>("fingerprint.hash");
        if (!string.IsNullOrEmpty(audioFingerprint))
            context.AddSignal(new VideoSignal
            {
                Key = "audio.fingerprint",
                Value = audioFingerprint,
                Source = Name,
                Tags = [VideoSignalTags.Audio]
            });

        var contentType = audioProfile.GetValue<string>("content.type"); // speech, music, mixed
        if (!string.IsNullOrEmpty(contentType))
            context.AddSignal(new VideoSignal
            {
                Key = "audio.content_type",
                Value = contentType,
                Source = Name,
                Tags = [VideoSignalTags.Audio]
            });

        // Add summary signals
        var totalWords = context.Utterances
            .Sum(u => u.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        context.AddSignals([
            new VideoSignal
            {
                Key = "transcription.utterance_count",
                Value = context.Utterances.Count,
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            },
            new VideoSignal
            {
                Key = "transcription.word_count",
                Value = totalWords,
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            },
            new VideoSignal
            {
                Key = "transcription.words_per_minute",
                Value = context.Metadata!.Duration > 0
                    ? totalWords / (context.Metadata.Duration / 60.0)
                    : 0,
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            },
            new VideoSignal
            {
                Key = "transcription.detected_language",
                Value = detectedLanguage ?? context.Utterances
                    .Where(u => !string.IsNullOrEmpty(u.Language))
                    .GroupBy(u => u.Language)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key ?? "unknown",
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            }
        ]);

        sw.Stop();

        // Emit wave completed signal with timing
        context.AddSignals([
            new VideoSignal
            {
                Key = $"{Name}.completed",
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Speech, "wave", "flow"]
            },
            new VideoSignal
            {
                Key = $"{Name}.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Speech, "timing", "persist"]
            }
        ]);

        context.ReportProgress("Transcription complete", 100);

        _logger.LogInformation(
            "Audio analysis complete: {Utterances} utterances, {Speakers} speakers, {Words} words in {Time}ms",
            context.Utterances.Count,
            context.Speakers.Count,
            totalWords,
            sw.ElapsedMilliseconds);
    }

    /// <summary>
    ///     Parse transcript segments from AudioSummarizer's JSON format.
    ///     AudioSummarizer stores segments as JSON in "transcription.full_data" with keys: start, end, text, confidence
    /// </summary>
    private List<TranscriptSegment> ParseTranscriptSegments(string? fullDataJson, string? defaultLanguage)
    {
        var segments = new List<TranscriptSegment>();

        if (string.IsNullOrEmpty(fullDataJson)) return segments;

        try
        {
            using var doc = JsonDocument.Parse(fullDataJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("segments", out var segmentsElement) &&
                segmentsElement.ValueKind == JsonValueKind.Array)
                foreach (var seg in segmentsElement.EnumerateArray())
                {
                    var segment = new TranscriptSegment
                    {
                        StartTime = seg.TryGetProperty("start", out var start) ? start.GetDouble() : 0,
                        EndTime = seg.TryGetProperty("end", out var end) ? end.GetDouble() : 0,
                        Text = seg.TryGetProperty("text", out var text) ? text.GetString() : null,
                        Confidence = seg.TryGetProperty("confidence", out var conf) &&
                                     conf.ValueKind == JsonValueKind.Number
                            ? conf.GetDouble()
                            : 0.7,
                        Language = defaultLanguage
                    };

                    segments.Add(segment);
                }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse transcript full_data JSON");
        }

        return segments;
    }
}

/// <summary>
///     Transcript segment from AudioSummarizer.
/// </summary>
public record TranscriptSegment
{
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public string? Text { get; init; }
    public double Confidence { get; init; }
    public string? Language { get; init; }
}

/// <summary>
///     Speaker information from diarization.
/// </summary>
public record SpeakerInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public float[]? Embedding { get; init; }
    public double TotalSpeakingTime { get; init; }
    public int UtteranceCount { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
///     Speaker segment from diarization.
/// </summary>
public record SpeakerSegment
{
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public string SpeakerId { get; init; } = "";
}
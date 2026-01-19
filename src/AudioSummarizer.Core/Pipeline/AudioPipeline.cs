using System.Text.Json;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Voice;
using Microsoft.Extensions.Logging;
using Mostlylucid.GraphRag.Extraction;
using Mostlylucid.Summarizer.Core.FileAnalysis;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace AudioSummarizer.Core.Pipeline;

/// <summary>
/// Pipeline implementation for audio files (MP3, WAV, M4A, FLAC, etc.).
/// Wraps the AudioWaveOrchestrator for transcription, speaker analysis, and acoustic profiling.
/// Extracts entities from transcripts and music tags for GraphRAG integration.
/// </summary>
public class AudioPipeline : PipelineBase
{
    private readonly IFileSummarizer _fileSummarizer;
    private readonly AudioWaveOrchestrator _orchestrator;
    private readonly ILogger<AudioPipeline> _logger;
    private OnnxNerService? _nerService;
    private bool _nerInitialized;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".wma", ".opus"
    };

    public AudioPipeline(
        AudioWaveOrchestrator orchestrator,
        IFileSummarizer fileSummarizer,
        ILogger<AudioPipeline> logger)
    {
        _orchestrator = orchestrator;
        _fileSummarizer = fileSummarizer;
        _logger = logger;
    }

    private async Task EnsureNerInitializedAsync(CancellationToken ct)
    {
        if (_nerInitialized) return;

        try
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            var modelsDir = Path.Combine(dataDir, "models", "bert-base-ner");

            // Auto-download NER models from HuggingFace if not present
            var progress = new Progress<string>(msg => _logger.LogDebug("NER: {Message}", msg));
            var downloaded = await NerModelRegistry.EnsureModelDownloadedAsync(
                modelsDir,
                NerModelRegistry.BertBaseNer,
                progress);

            if (downloaded)
            {
                _nerService = new OnnxNerService(modelsDir, NerModelRegistry.BertBaseNer);
                await _nerService.InitializeAsync(ct);
                _logger.LogDebug("NER service initialized for audio transcript entity extraction");
            }
            else
            {
                _logger.LogWarning("NER models not available, transcript entity extraction disabled");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize NER service for audio pipeline");
        }

        _nerInitialized = true;
    }

    /// <inheritdoc />
    public override string PipelineId => "audio";

    /// <inheritdoc />
    public override string Name => "Audio Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => AudioExtensions;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing audio: {FilePath}", filePath);

        // Extract universal file metadata first
        var fileMetadata = await _fileSummarizer.ExtractMetadataAsync(filePath, ct);
        var fileMetadataDict = fileMetadata.ToSearchMetadata();

        progress?.Report(new PipelineProgress("Analyzing", "Running audio analysis waves", 10));

        // Run the wave orchestrator to analyze the audio
        var profile = await _orchestrator.AnalyzeAsync(filePath, ct);

        progress?.Report(new PipelineProgress("Extracting", "Building content chunks", 80));

        var chunks = new List<ContentChunk>();
        var chunkIndex = 0;

        // Extract transcription if available
        var transcriptionText = profile.GetValue<string>("transcription.text");
        var transcriptionFullData = profile.GetValue<string>("transcription.full_data");

        if (!string.IsNullOrWhiteSpace(transcriptionText))
        {
            var confidence = profile.GetValue<double?>("transcription.confidence") ?? 0.0;
            var backend = profile.GetValue<string>("transcription.backend") ?? "unknown";
            var segmentCount = profile.GetValue<int?>("transcription.segment_count") ?? 0;

            // Generate SRT format with diarization if available
            var (srtContent, vttContent) = GenerateSrtWithDiarization(profile, transcriptionFullData);

            var transcriptMetadata = new Dictionary<string, object?>(fileMetadataDict)
            {
                ["source"] = "transcription",
                ["backend"] = backend,
                ["segment_count"] = segmentCount,
                ["transcription.confidence"] = confidence,
                ["transcription.srt"] = srtContent,
                ["transcription.vtt"] = vttContent,
                ["transcription.full_data"] = transcriptionFullData
            };
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = transcriptionText,
                ContentType = ContentType.Transcript,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(transcriptionText),
                Confidence = confidence,
                Metadata = transcriptMetadata
            });
        }

        // Extract content classification for graph building
        var contentType = profile.GetValue<string>("audio.content_type"); // speech|music|mixed|silence
        var speechProbability = profile.GetValue<double?>("audio.speech_probability");
        var musicProbability = profile.GetValue<double?>("audio.music_probability");

        // Initialize NER for transcript entity extraction
        await EnsureNerInitializedAsync(ct);

        // Build rich metadata for cross-modal graph relationships
        var allEntities = new List<string>();
        var graphMetadata = new Dictionary<string, object?>
        {
            ["content.audio_type"] = contentType,
            ["content.speech_probability"] = speechProbability,
            ["content.music_probability"] = musicProbability
        };

        // Extract entities from transcription text using NER
        if (!string.IsNullOrWhiteSpace(transcriptionText) && _nerService != null)
        {
            try
            {
                var nerEntities = await _nerService.ExtractSpansAsync(transcriptionText);
                _logger.LogDebug("NER extracted {Count} entities from transcript", nerEntities.Count);

                foreach (var entity in nerEntities)
                {
                    if (!allEntities.Contains(entity.Text, StringComparer.OrdinalIgnoreCase))
                    {
                        allEntities.Add(entity.Text);
                    }
                }
                graphMetadata["ner_entity_count"] = nerEntities.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract entities from transcript");
            }
        }

        // Add speaker information if available
        var speakerCount = profile.GetValue<int?>("speaker.count");
        if (speakerCount.HasValue && speakerCount > 0)
        {
            graphMetadata["content.speaker_count"] = speakerCount;
            // Add speaker IDs to entities for relationship building
            for (int i = 0; i < speakerCount.Value; i++)
            {
                allEntities.Add($"Speaker_{i + 1}");
            }
        }

        // Add music analysis SIGNALS for graph features
        // These enable cross-modal linking (audio with similar BPM, key, mood, etc.)
        var bpm = profile.GetValue<double?>("music.bpm");
        var bpmCategory = profile.GetValue<string>("music.tempo_category"); // e.g., "moderate", "fast"
        var key = profile.GetValue<string>("music.key");
        var mood = profile.GetValue<string>("music.mood");
        var energy = profile.GetValue<double?>("music.energy_db");
        var genre = profile.GetValue<string>("music.genre");
        var hasVocals = profile.GetValue<bool?>("music.has_vocals");
        var hasInstrumentals = profile.GetValue<bool?>("music.instrumental");

        // Store music signals as graph-linkable metadata
        if (bpm.HasValue)
        {
            graphMetadata["content.bpm"] = bpm;
            graphMetadata["content.tempo_category"] = bpmCategory;
            // Add tempo category as entity for linking similar tempo audio
            if (!string.IsNullOrEmpty(bpmCategory))
                allEntities.Add($"Tempo:{bpmCategory}");
        }

        if (!string.IsNullOrEmpty(key))
        {
            graphMetadata["content.key"] = key;
            allEntities.Add($"Key:{key}"); // Link audio in same musical key
        }

        if (!string.IsNullOrEmpty(mood))
        {
            graphMetadata["content.mood"] = mood;
            allEntities.Add($"Mood:{mood}"); // Link audio with similar mood
        }

        if (energy.HasValue)
        {
            graphMetadata["content.energy_db"] = energy;
            // Categorize energy level for graph linking
            var energyLevel = energy > -6 ? "high" : energy > -15 ? "medium" : "low";
            graphMetadata["content.energy_level"] = energyLevel;
            allEntities.Add($"Energy:{energyLevel}");
        }

        if (!string.IsNullOrEmpty(genre))
        {
            graphMetadata["content.genre"] = genre;
            allEntities.Add($"Genre:{genre}"); // Link audio of same genre
        }

        if (hasVocals == true)
            allEntities.Add("Vocals");
        if (hasInstrumentals == true)
            allEntities.Add("Instrumental");

        // Add fingerprint for deduplication/similarity
        var fingerprintHash = profile.GetValue<string>("audio.fingerprint.hash");
        if (!string.IsNullOrEmpty(fingerprintHash))
        {
            graphMetadata["content.fingerprint"] = fingerprintHash;
        }

        // Add acoustic features for similarity matching
        var duration = profile.GetValue<double?>("audio.duration_seconds");
        var sampleRate = profile.GetValue<int?>("audio.sample_rate");
        var channels = profile.GetValue<int?>("audio.channels");

        graphMetadata["content.duration_seconds"] = duration;
        graphMetadata["content.sample_rate"] = sampleRate;
        graphMetadata["content.channels"] = channels;

        // Store entities in the standard format
        graphMetadata["content.entities"] = allEntities;

        // Create a summary chunk with all the rich metadata for graph building
        if (chunks.Count > 0)
        {
            var mainChunk = chunks[0];
            foreach (var (key2, value) in graphMetadata)
            {
                mainChunk.Metadata[key2] = value;
            }
        }
        else
        {
            // No transcription - create metadata-only chunk with file metadata
            var metadataText = BuildMetadataText(filePath, profile);
            // Merge file metadata into graph metadata
            foreach (var kvp in fileMetadataDict)
                graphMetadata[kvp.Key] = kvp.Value;

            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = metadataText,
                ContentType = ContentType.AudioMetadata,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(metadataText),
                Metadata = graphMetadata
            });
        }

        _logger.LogInformation("Processed audio: {ChunkCount} chunks, {SignalCount} signals (file: {Size})",
            chunks.Count, profile.Signals.Count, fileMetadata.SizeFormatted);

        return chunks;
    }

    private static string BuildMetadataText(string filePath, AudioProfile profile)
    {
        var duration = profile.GetValue<double?>("audio.duration_seconds") ?? 0;
        var format = profile.GetValue<string>("audio.format") ?? Path.GetExtension(filePath).TrimStart('.');
        var contentType = profile.GetValue<string>("audio.content_type") ?? "unknown";
        var sampleRate = profile.GetValue<int?>("audio.sample_rate") ?? 0;

        var durationSpan = TimeSpan.FromSeconds(duration);
        return $"Audio: {Path.GetFileName(filePath)}, Format: {format.ToUpperInvariant()}, " +
               $"Duration: {durationSpan:mm\\:ss}, Type: {contentType}, Sample Rate: {sampleRate} Hz";
    }

    /// <summary>
    /// Generate SRT and WebVTT formats with speaker diarization merged in.
    /// </summary>
    private (string? Srt, string? Vtt) GenerateSrtWithDiarization(AudioProfile profile, string? transcriptionJson)
    {
        if (string.IsNullOrWhiteSpace(transcriptionJson))
            return (null, null);

        try
        {
            // Parse transcript segments from JSON
            var transcriptData = JsonSerializer.Deserialize<TranscriptJsonData>(transcriptionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (transcriptData?.Segments == null || transcriptData.Segments.Count == 0)
                return (null, null);

            var segments = transcriptData.Segments.Select(s => new TranscriptSegment
            {
                Start = s.Start,
                End = s.End,
                Text = s.Text ?? "",
                Confidence = s.Confidence ?? 0
            }).ToList();

            // Parse speaker turns from diarization (if available)
            List<SpeakerTurn>? speakerTurns = null;
            var speakerTurnsJson = profile.GetValue<string>("speaker.turns");
            if (!string.IsNullOrWhiteSpace(speakerTurnsJson))
            {
                var turnsData = JsonSerializer.Deserialize<List<SpeakerTurnJsonData>>(speakerTurnsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                speakerTurns = turnsData?.Select(t => new SpeakerTurn
                {
                    SpeakerId = t.SpeakerId ?? "UNKNOWN",
                    StartSeconds = t.StartSeconds,
                    EndSeconds = t.EndSeconds,
                    Confidence = t.Confidence ?? 0
                }).ToList();
            }

            // Generate SRT and VTT using the formatter
            var formatter = new SrtFormatter();
            var srt = formatter.FormatToSrt(segments, speakerTurns, includeSpeakerLabels: speakerTurns?.Count > 0);
            var vtt = formatter.FormatToWebVtt(segments, speakerTurns, includeSpeakerLabels: speakerTurns?.Count > 0);

            return (srt, vtt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate SRT/VTT from transcript data");
            return (null, null);
        }
    }

    // JSON deserialization helpers
    private class TranscriptJsonData
    {
        public string? Text { get; set; }
        public string? Language { get; set; }
        public double? Confidence { get; set; }
        public List<TranscriptSegmentJson>? Segments { get; set; }
    }

    private class TranscriptSegmentJson
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public double? Confidence { get; set; }
    }

    private class SpeakerTurnJsonData
    {
        public string? SpeakerId { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public double? Confidence { get; set; }
    }
}

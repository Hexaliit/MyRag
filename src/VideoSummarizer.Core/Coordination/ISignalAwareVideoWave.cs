using VideoSummarizer.Core.Waves;

namespace VideoSummarizer.Core.Coordination;

/// <summary>
///     Interface for waves that declare their signal contracts explicitly.
///     Enables dynamic wave coordination based on signal dependencies.
/// </summary>
public interface ISignalAwareVideoWave
{
    /// <summary>
    ///     Signals this wave requires before it can run.
    ///     Wave will be skipped if any required signal is missing.
    /// </summary>
    IReadOnlyList<string> RequiredSignals { get; }

    /// <summary>
    ///     Signals this wave can optionally use if available.
    ///     Wave runs even if optional signals are missing.
    /// </summary>
    IReadOnlyList<string> OptionalSignals { get; }

    /// <summary>
    ///     Signals this wave emits on successful completion.
    /// </summary>
    IReadOnlyList<string> EmittedSignals { get; }

    /// <summary>
    ///     Signals emitted when the wave starts processing.
    /// </summary>
    IReadOnlyList<string> StartSignals => [$"wave.started.{((IVideoWave)this).Name}"];

    /// <summary>
    ///     Signals emitted when the wave fails.
    /// </summary>
    IReadOnlyList<string> FailureSignals => [$"wave.failed.{((IVideoWave)this).Name}"];

    /// <summary>
    ///     Cache keys this wave produces for downstream waves.
    /// </summary>
    IReadOnlyList<string> CacheEmits { get; }

    /// <summary>
    ///     Cache keys this wave consumes from upstream waves.
    /// </summary>
    IReadOnlyList<string> CacheUses { get; }
}

/// <summary>
///     Signal event for reactive wave composition.
/// </summary>
public readonly record struct VideoSignalEvent(
    string Signal,
    object? Value,
    string Source,
    double Confidence,
    DateTimeOffset Timestamp
)
{
    public bool Is(string name)
    {
        return Signal.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    public bool StartsWith(string prefix)
    {
        return Signal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Well-known signal keys for VideoSummarizer.
///     Using constants ensures consistency across waves.
/// </summary>
public static class VideoSignals
{
    // NormalizeWave signals
    public const string VideoDuration = "video.duration";
    public const string VideoFps = "video.fps";
    public const string VideoWidth = "video.width";
    public const string VideoHeight = "video.height";
    public const string VideoCodec = "video.codec";
    public const string VideoContentHash = "video.content_hash";
    public const string AudioCodec = "audio.codec";
    public const string AudioExtractedPath = "audio.extracted_path";
    public const string VideoNormalized = "video.normalized";

    // Shot detection signals
    public const string ShotsDetected = "shots.detected";
    public const string ShotsCount = "shots.count";
    public const string ShotsAvgDuration = "shots.avg_duration";
    public const string ShotsDetectionMethod = "shots.detection_method";

    // I-frame detection signals
    public const string IframesDetected = "keyframes.iframes_detected";
    public const string IframesCount = "keyframes.iframes_count";

    // Keyframe selection signals
    public const string KeyframesSelected = "keyframes.selected";
    public const string KeyframesSelectedCount = "keyframes.selected_count";

    // Thumbnail extraction signals
    public const string ThumbnailsExtracted = "keyframes.thumbnails_extracted";

    // Deduplication signals
    public const string KeyframesDeduplicated = "keyframes.deduplicated";
    public const string KeyframesDuplicatesSkipped = "keyframes.duplicates_skipped";

    // Full-res extraction signals
    public const string KeyframesExtracted = "keyframes.extracted";
    public const string KeyframesCount = "keyframes.count";

    // CLIP embedding signals
    public const string ClipEmbeddingsReady = "clip.embeddings_ready";
    public const string ClipEmbeddingsCount = "clip.embeddings_count";
    public const string ClipBatchSize = "clip.batch_size";

    // ImageSummarizer analysis signals
    public const string KeyframesAnalyzed = "keyframes.analyzed";
    public const string OcrExtracted = "ocr.extracted";
    public const string CaptionsGenerated = "captions.generated";

    // Transcription signals
    public const string TranscriptionComplete = "transcription.complete";
    public const string TranscriptionUtteranceCount = "transcription.utterance_count";
    public const string TranscriptionWordCount = "transcription.word_count";
    public const string DiarizationSpeakerCount = "diarization.speaker_count";

    // Scene clustering signals
    public const string ScenesDetected = "scenes.detected";
    public const string ScenesCreated = "scenes.created"; // Alias for backward compat
    public const string SceneCount = "scene.count";
    public const string SceneClusteringMethod = "scene.clustering_method";

    // Evidence generation signals
    public const string EvidenceGenerated = "evidence.generated";
    public const string EvidenceTotalCount = "evidence.total_count";
    public const string EvidenceWithEmbeddings = "evidence.with_embeddings";

    // Escalation signals
    public const string EscalationRequired = "escalation.required";
    public const string EscalationVisionLlm = "escalation.vision_llm";
    public const string EscalationOcrRetry = "escalation.ocr_retry";
}

/// <summary>
///     Escalation condition for triggering additional analysis.
/// </summary>
public record EscalationCondition
{
    /// <summary>Signal to check for escalation trigger.</summary>
    public required string Signal { get; init; }

    /// <summary>Expected value to trigger escalation.</summary>
    public object? Value { get; init; }

    /// <summary>Condition expression (e.g., "> 0.8", "IsNullOrEmpty").</summary>
    public string? Condition { get; init; }
}

/// <summary>
///     Escalation rule for a wave.
/// </summary>
public record EscalationRule
{
    /// <summary>Target wave or service to escalate to.</summary>
    public required string Target { get; init; }

    /// <summary>Conditions that trigger this escalation.</summary>
    public List<EscalationCondition> When { get; init; } = [];

    /// <summary>Conditions that skip this escalation.</summary>
    public List<EscalationCondition> SkipWhen { get; init; } = [];
}
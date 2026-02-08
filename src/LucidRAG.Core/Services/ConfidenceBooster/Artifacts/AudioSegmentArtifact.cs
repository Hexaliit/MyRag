namespace LucidRAG.Core.Services.ConfidenceBooster.Artifacts;

/// <summary>
///     Artifact representing a bounded audio segment for LLM-based transcription refinement.
/// </summary>
public class AudioSegmentArtifact : IArtifact
{
    /// <summary>
    ///     Base64-encoded WAV audio segment (16kHz mono recommended).
    /// </summary>
    public required string Base64Audio { get; init; }

    /// <summary>
    ///     Time range of this segment [startSeconds, endSeconds].
    /// </summary>
    public required double[] TimeRange { get; init; }

    /// <summary>
    ///     Duration in seconds.
    /// </summary>
    public double Duration => TimeRange.Length == 2 ? TimeRange[1] - TimeRange[0] : 0;

    /// <summary>
    ///     Original low-confidence transcription from Whisper/STT.
    /// </summary>
    public string? OriginalTranscription { get; init; }

    /// <summary>
    ///     Context from surrounding segments (for disambiguation).
    /// </summary>
    public string? ContextBefore { get; init; }

    public string? ContextAfter { get; init; }

    /// <summary>
    ///     Task type for LLM (transcription_refinement, technical_term_correction, speaker_attribution).
    /// </summary>
    public required string TaskType { get; init; }

    public required string ArtifactId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string SignalName { get; init; }
    public required double OriginalConfidence { get; init; }
    public string ArtifactType => "audio_segment";
    public Dictionary<string, object> Metadata { get; init; } = new();
}
namespace AudioSummarizer.Core.Services.Transcription;

/// <summary>
///     Service for transcribing audio to text with optional speaker timestamps
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    ///     Provider name (e.g., "Whisper.NET", "Ollama")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Transcribe an audio file to text
    /// </summary>
    /// <param name="audioPath">Path to audio file</param>
    /// <param name="language">Language code (e.g., "en", "es") or null for auto-detect</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Audio transcript with timestamped segments</returns>
    Task<AudioTranscript> TranscribeAsync(string audioPath, string? language = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Check if this transcription service is available
    /// </summary>
    /// <returns>True if service can be used, false otherwise</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Audio transcript with timestamped segments
/// </summary>
public sealed class AudioTranscript
{
    /// <summary>
    ///     Full transcript text
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    ///     Timestamped segments
    /// </summary>
    public required List<TranscriptSegment> Segments { get; init; }

    /// <summary>
    ///     Language detected or specified
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    ///     Provider that generated this transcript
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    ///     Overall confidence score (0.0 to 1.0)
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    ///     When the transcript was generated
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; init; }
}

/// <summary>
///     Timestamped transcript segment
/// </summary>
public sealed class TranscriptSegment
{
    /// <summary>
    ///     Start time in seconds
    /// </summary>
    public required double Start { get; init; }

    /// <summary>
    ///     End time in seconds
    /// </summary>
    public required double End { get; init; }

    /// <summary>
    ///     Segment text
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    ///     Confidence score for this segment (0.0 to 1.0)
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    ///     Speaker ID (if diarization is available)
    /// </summary>
    public string? SpeakerId { get; init; }

    /// <summary>
    ///     Format as markdown with timestamp
    /// </summary>
    public string ToMarkdown()
    {
        return $"[{Start:F1}s - {End:F1}s] {Text}";
    }

    /// <summary>
    ///     Format as SRT subtitle entry
    /// </summary>
    public string ToSrt(int index)
    {
        var startTime = TimeSpan.FromSeconds(Start);
        var endTime = TimeSpan.FromSeconds(End);
        return $"{index}\n{startTime:hh\\:mm\\:ss\\,fff} --> {endTime:hh\\:mm\\:ss\\,fff}\n{Text}\n";
    }
}
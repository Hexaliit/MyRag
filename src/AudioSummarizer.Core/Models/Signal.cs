namespace AudioSummarizer.Core.Models;

/// <summary>
///     Represents a single signal extracted from audio analysis.
///     Signals can be deterministic (hash, duration) or probabilistic (transcription, speaker detection).
/// </summary>
public sealed class Signal
{
    /// <summary>
    ///     Fully qualified signal name (e.g., "audio.hash.sha256", "transcription.confidence")
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Signal value (can be string, number, boolean, or complex object)
    /// </summary>
    public required object Value { get; init; }

    /// <summary>
    ///     Optional confidence score for probabilistic signals (0.0 to 1.0)
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    ///     Signal type for categorization
    /// </summary>
    public SignalType Type { get; init; } = SignalType.Metadata;

    /// <summary>
    ///     Source that generated this signal (e.g., "IdentityWave", "TranscriptionWave")
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    ///     When the signal was generated
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString()
    {
        return $"{Name} = {Value}" + (Confidence.HasValue ? $" (confidence: {Confidence:F2})" : "");
    }
}

/// <summary>
///     Signal classification
/// </summary>
public enum SignalType
{
    /// <summary>
    ///     File metadata (format, duration, sample rate)
    /// </summary>
    Metadata,

    /// <summary>
    ///     Cryptographic hash or fingerprint
    /// </summary>
    Identity,

    /// <summary>
    ///     Acoustic measurement (RMS, spectral features)
    /// </summary>
    Acoustic,

    /// <summary>
    ///     Content classification (speech vs music)
    /// </summary>
    Classification,

    /// <summary>
    ///     Transcription output
    /// </summary>
    Transcription,

    /// <summary>
    ///     Speaker-related signal
    /// </summary>
    Speaker,

    /// <summary>
    ///     Sentiment or emotion
    /// </summary>
    Sentiment,

    /// <summary>
    ///     Embedding vector (voice, audio features)
    /// </summary>
    Embedding,

    /// <summary>
    ///     Evidence artifact (file path to extracted content)
    /// </summary>
    Evidence
}
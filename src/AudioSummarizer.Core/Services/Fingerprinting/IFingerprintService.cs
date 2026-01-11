using AudioSummarizer.Core.Models;

namespace AudioSummarizer.Core.Services.Fingerprinting;

/// <summary>
/// Service for generating perceptual audio fingerprints for deduplication and similarity detection
/// </summary>
public interface IFingerprintService
{
    /// <summary>
    /// Provider name (e.g., "PureNet", "Chromaprint")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Generate a fingerprint from an audio file
    /// </summary>
    /// <param name="audioPath">Path to audio file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Audio fingerprint</returns>
    Task<AudioFingerprint> GenerateFingerprintAsync(string audioPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculate similarity between two fingerprints (0.0 = completely different, 1.0 = identical)
    /// </summary>
    /// <param name="fingerprint1">First fingerprint</param>
    /// <param name="fingerprint2">Second fingerprint</param>
    /// <returns>Similarity score (0.0 to 1.0)</returns>
    double CalculateSimilarity(AudioFingerprint fingerprint1, AudioFingerprint fingerprint2);
}

/// <summary>
/// Audio fingerprint for perceptual deduplication
/// </summary>
public sealed class AudioFingerprint
{
    /// <summary>
    /// Fingerprint type (e.g., "spectral_peaks", "chromaprint")
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Fingerprint hash (hex string)
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Raw fingerprint data (bit array or byte array)
    /// </summary>
    public byte[]? RawData { get; init; }

    /// <summary>
    /// Duration of the audio file in seconds
    /// </summary>
    public double DurationSeconds { get; init; }

    /// <summary>
    /// When the fingerprint was generated
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Provider that generated this fingerprint
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Compare two fingerprints for equality
    /// </summary>
    public bool Equals(AudioFingerprint? other)
    {
        if (other is null) return false;
        return Type == other.Type && Hash == other.Hash;
    }

    public override bool Equals(object? obj) => Equals(obj as AudioFingerprint);
    public override int GetHashCode() => HashCode.Combine(Type, Hash);
}

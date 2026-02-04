namespace AudioSummarizer.Core.Services.SourceSeparation;

/// <summary>
///     Audio source separation service for isolating vocals from instrumentals.
///     Enables lyrics transcription from music and better diarization of singing.
///     Implementation options:
///     - Demucs v4 (ONNX) - State-of-the-art, requires ONNX model from https://github.com/sevagh/demucs.onnx
///     - Spleeter (Python interop) - Fast, good quality
///     - Open-Unmix (ONNX) - Open source
/// </summary>
public interface ISourceSeparationService
{
    /// <summary>
    ///     Check if source separation is available (model downloaded, etc.)
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     Separate audio into stems (vocals, drums, bass, other)
    /// </summary>
    /// <param name="audioPath">Input audio file path</param>
    /// <param name="outputDirectory">Directory to write separated stems</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing paths to separated stems</returns>
    Task<SeparationResult> SeparateAsync(
        string audioPath,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Extract only the vocal track (for speech/lyrics transcription)
    /// </summary>
    /// <param name="audioPath">Input audio file path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to extracted vocals WAV file</returns>
    Task<string> ExtractVocalsAsync(
        string audioPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Extract only the instrumental track (vocals removed)
    /// </summary>
    /// <param name="audioPath">Input audio file path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to instrumentals WAV file</returns>
    Task<string> ExtractInstrumentalsAsync(
        string audioPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Result of audio source separation
/// </summary>
public class SeparationResult
{
    /// <summary>
    ///     Whether separation succeeded
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Path to vocals stem (singing, speech)
    /// </summary>
    public string? VocalsPath { get; set; }

    /// <summary>
    ///     Path to drums stem
    /// </summary>
    public string? DrumsPath { get; set; }

    /// <summary>
    ///     Path to bass stem
    /// </summary>
    public string? BassPath { get; set; }

    /// <summary>
    ///     Path to other/accompaniment stem
    /// </summary>
    public string? OtherPath { get; set; }

    /// <summary>
    ///     Path to instrumentals (all non-vocals combined)
    /// </summary>
    public string? InstrumentalsPath { get; set; }

    /// <summary>
    ///     Processing time
    /// </summary>
    public TimeSpan ProcessingTime { get; set; }

    /// <summary>
    ///     All output stem paths
    /// </summary>
    public Dictionary<string, string> Stems { get; set; } = new();
}

/// <summary>
///     Stem types for source separation
/// </summary>
public enum StemType
{
    Vocals,
    Drums,
    Bass,
    Other,
    Instrumentals // Combined non-vocals
}
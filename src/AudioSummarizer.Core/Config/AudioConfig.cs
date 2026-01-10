namespace AudioSummarizer.Core.Config;

/// <summary>
/// Configuration for AudioSummarizer
/// </summary>
public sealed class AudioConfig
{
    public const string SectionName = "Audio";

    /// <summary>
    /// Transcription backend to use
    /// </summary>
    public TranscriptionBackend TranscriptionBackend { get; set; } = TranscriptionBackend.Whisper;

    /// <summary>
    /// Whisper.NET configuration
    /// </summary>
    public WhisperConfig Whisper { get; set; } = new();

    /// <summary>
    /// Ollama configuration
    /// </summary>
    public OllamaConfig Ollama { get; set; } = new();

    /// <summary>
    /// Supported audio file formats
    /// </summary>
    public string[] SupportedFormats { get; set; } = { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".wma", ".aac" };

    /// <summary>
    /// Maximum audio file size in MB
    /// </summary>
    public int MaxFileSizeMB { get; set; } = 500;

    /// <summary>
    /// Enable speaker diarization (Phase 5)
    /// </summary>
    public bool EnableSpeakerDiarization { get; set; } = false;

    /// <summary>
    /// Enable voice embeddings for speaker similarity (Phase 4)
    /// </summary>
    public bool EnableVoiceEmbeddings { get; set; } = false;

    /// <summary>
    /// Enable sentiment analysis (future)
    /// </summary>
    public bool EnableSentimentAnalysis { get; set; } = false;

    /// <summary>
    /// Pipeline feature toggles
    /// </summary>
    public PipelineConfig Pipeline { get; set; } = new();

    /// <summary>
    /// Fingerprint provider
    /// </summary>
    public FingerprintProvider FingerprintProvider { get; set; } = FingerprintProvider.PureNet;

    /// <summary>
    /// Verbose logging for CLI
    /// </summary>
    public bool Verbose { get; set; } = false;
}

/// <summary>
/// Transcription backend options
/// </summary>
public enum TranscriptionBackend
{
    /// <summary>
    /// Local Whisper.NET (offline, default)
    /// </summary>
    Whisper,

    /// <summary>
    /// Ollama HTTP service
    /// </summary>
    Ollama,

    /// <summary>
    /// Auto-detect (try Whisper first, fallback to Ollama)
    /// </summary>
    Auto
}

/// <summary>
/// Fingerprint provider options
/// </summary>
public enum FingerprintProvider
{
    /// <summary>
    /// Pure .NET spectral peak hashing (default, cross-platform)
    /// </summary>
    PureNet,

    /// <summary>
    /// Chromaprint via P/Invoke (optional, more accurate)
    /// </summary>
    Chromaprint
}

/// <summary>
/// Whisper.NET configuration
/// </summary>
public sealed class WhisperConfig
{
    /// <summary>
    /// Path to Whisper model file (auto-downloads if missing)
    /// </summary>
    public string ModelPath { get; set; } = "./models/whisper-base.en.bin";

    /// <summary>
    /// Whisper model size (tiny, base, small, medium, large)
    /// </summary>
    public string ModelSize { get; set; } = "base";

    /// <summary>
    /// Language code (e.g., "en", "es", "fr")
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Use GPU acceleration if available
    /// </summary>
    public bool UseGpu { get; set; } = false;
}

/// <summary>
/// Ollama configuration
/// </summary>
public sealed class OllamaConfig
{
    /// <summary>
    /// Ollama base URL
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Ollama model for transcription
    /// </summary>
    public string Model { get; set; } = "whisper";
}

/// <summary>
/// Pipeline feature toggles
/// </summary>
public sealed class PipelineConfig
{
    /// <summary>
    /// Enable perceptual fingerprinting (Phase 2)
    /// </summary>
    public bool EnableFingerprinting { get; set; } = true;

    /// <summary>
    /// Enable acoustic profiling (RMS, spectral features)
    /// </summary>
    public bool EnableAcousticProfiling { get; set; } = true;

    /// <summary>
    /// Enable content classification (speech vs music)
    /// </summary>
    public bool EnableContentClassification { get; set; } = true;
}

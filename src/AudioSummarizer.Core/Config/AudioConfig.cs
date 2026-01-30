namespace AudioSummarizer.Core.Config;

/// <summary>
/// Transcription backend options
/// </summary>
public enum TranscriptionBackend
{
    Whisper,
    Ollama,
    Auto
}

/// <summary>
/// Fingerprint provider options
/// </summary>
public enum FingerprintProvider
{
    PureNet,
    Chromaprint
}

/// <summary>
/// Configuration for AudioSummarizer
/// </summary>
public class AudioConfig
{
    public const string SectionName = "Audio";

    /// <summary>
    /// Transcription backend to use
    /// </summary>
    public TranscriptionBackend TranscriptionBackend { get; set; } = TranscriptionBackend.Whisper;

    /// <summary>
    /// Supported audio formats
    /// </summary>
    public string[] SupportedFormats { get; set; } = new[] { ".mp3", ".wav", ".m4a", ".flac", ".ogg" };

    /// <summary>
    /// Verbose logging
    /// </summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Fingerprint provider
    /// </summary>
    public FingerprintProvider FingerprintProvider { get; set; } = FingerprintProvider.PureNet;

    /// <summary>
    /// Enable voice embeddings
    /// </summary>
    public bool EnableVoiceEmbeddings { get; set; } = false;

    /// <summary>
    /// Enable speaker diarization
    /// </summary>
    public bool EnableSpeakerDiarization { get; set; } = false;

    /// <summary>
    /// Enable source separation (Demucs for music)
    /// Note: Requires ~210MB model download, disabled by default
    /// </summary>
    public bool EnableSourceSeparation { get; set; } = false;

    /// <summary>
    /// Whisper transcription settings
    /// </summary>
    public WhisperConfig Whisper { get; set; } = new();

    /// <summary>
    /// Ollama transcription settings
    /// </summary>
    public OllamaConfig Ollama { get; set; } = new();

    /// <summary>
    /// Fingerprinting settings
    /// </summary>
    public FingerprintConfig Fingerprint { get; set; } = new();

    /// <summary>
    /// Pipeline settings
    /// </summary>
    public PipelineConfig Pipeline { get; set; } = new();

    /// <summary>
    /// Voice embedding settings
    /// </summary>
    public VoiceEmbeddingConfig VoiceEmbedding { get; set; } = new();
}

public class WhisperConfig
{
    /// <summary>
    /// Path to Whisper GGML model file
    /// </summary>
    public string ModelPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "lucidrag", "models", "whisper-tiny.en.bin");

    /// <summary>
    /// Model size: tiny, base, small, medium, large
    /// </summary>
    public string ModelSize { get; set; } = "tiny";

    /// <summary>
    /// Language code (en, es, fr, etc.) or "auto"
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Number of CPU threads to use
    /// </summary>
    public int Threads { get; set; } = Environment.ProcessorCount / 2;
}

public class OllamaConfig
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

public class FingerprintConfig
{
    /// <summary>
    /// Fingerprint provider: PureNet or Chromaprint
    /// </summary>
    public string Provider { get; set; } = "PureNet";
}

public class PipelineConfig
{
    /// <summary>
    /// Enable fingerprinting wave
    /// </summary>
    public bool EnableFingerprinting { get; set; } = true;

    /// <summary>
    /// Enable acoustic profiling wave
    /// </summary>
    public bool EnableAcousticProfiling { get; set; } = true;

    /// <summary>
    /// Enable content classification wave
    /// </summary>
    public bool EnableContentClassification { get; set; } = true;

    /// <summary>
    /// Enable transcription wave
    /// </summary>
    public bool EnableTranscription { get; set; } = true;
}

public class VoiceEmbeddingConfig
{
    /// <summary>
    /// Path to ECAPA-TDNN ONNX model file
    /// </summary>
    public string ModelPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "lucidrag", "models", "ecapa-tdnn.onnx");

    /// <summary>
    /// Voice embedding dimension (192 or 512 depending on model)
    /// </summary>
    public int EmbeddingDimension { get; set; } = 192;

    /// <summary>
    /// Minimum audio duration in seconds for reliable embedding
    /// </summary>
    public double MinDurationSeconds { get; set; } = 1.0;

    /// <summary>
    /// Whether to persist voice embeddings to the vector store (when available).
    /// </summary>
    public bool PersistEmbeddings { get; set; } = true;

    /// <summary>
    /// Vector store collection name for persisted audio embeddings.
    /// </summary>
    public string CollectionName { get; set; } = "audio_embeddings";
}

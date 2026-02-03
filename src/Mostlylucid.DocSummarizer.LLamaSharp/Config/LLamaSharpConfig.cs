namespace Mostlylucid.DocSummarizer.LLamaSharp.Config;

/// <summary>
///     Configuration for local GGUF model inference via LLamaSharp.
/// </summary>
public record LLamaSharpConfig
{
    public const string SectionName = "LLamaSharp";

    /// <summary>
    ///     Whether LLamaSharp local inference is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     Directory to store downloaded GGUF model files.
    ///     Supports ~ for home directory expansion.
    /// </summary>
    public string ModelDirectory { get; init; } = "~/.doomsummarizer/models/llm";

    /// <summary>
    ///     Name of the synthesis (main) model for digest generation and Q&amp;A.
    /// </summary>
    public string SynthesisModel { get; init; } = "phi-4-mini-instruct";

    /// <summary>
    ///     Name of the sentinel (fast) model for classification, JSON extraction, and routing.
    /// </summary>
    public string SentinelModel { get; init; } = "qwen2.5-0.5b-instruct";

    /// <summary>
    ///     GGUF quantization level for downloaded models.
    /// </summary>
    public string Quantization { get; init; } = "Q4_K_M";

    /// <summary>
    ///     Context size in tokens. Kept conservative to limit memory usage.
    /// </summary>
    public uint ContextSize { get; init; } = 4096;

    /// <summary>
    ///     Number of model layers to offload to GPU.
    ///     -1 = auto (all layers to GPU if available), 0 = CPU only.
    /// </summary>
    public int GpuLayerCount { get; init; } = -1;

    /// <summary>
    ///     Batch size for prompt processing.
    /// </summary>
    public int BatchSize { get; init; } = 512;

    /// <summary>
    ///     Whether to automatically download models if not present locally.
    /// </summary>
    public bool AutoDownload { get; init; } = true;

    /// <summary>
    ///     Resolve the model directory path, expanding ~ to the user's home directory.
    /// </summary>
    public string ResolvedModelDirectory =>
        ModelDirectory.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ModelDirectory[2..])
            : ModelDirectory;
}

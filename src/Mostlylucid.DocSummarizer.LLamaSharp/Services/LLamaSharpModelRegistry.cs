namespace Mostlylucid.DocSummarizer.LLamaSharp.Services;

/// <summary>
///     Registry of known GGUF models with download URLs, sizes, and capabilities.
/// </summary>
public static class LLamaSharpModelRegistry
{
    /// <summary>
    ///     Information about a downloadable GGUF model.
    /// </summary>
    public record ModelInfo(
        string Name,
        string DisplayName,
        string HuggingFaceRepo,
        string Filename,
        long ExpectedSizeBytes,
        int ContextWindow,
        string Role);

    /// <summary>
    ///     All known models available for download.
    /// </summary>
    public static readonly ModelInfo[] Models =
    [
        // Sentinel models (fast classification, JSON extraction, query routing)
        new("qwen2.5-0.5b-instruct", "Qwen 2.5 0.5B Instruct",
            "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
            "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            397_000_000, 32768, "sentinel"),

        // Synthesis models (digests, articles, Q&A answers)
        new("phi-4-mini-instruct", "Phi-4 Mini 3.8B Instruct",
            "unsloth/Phi-4-mini-instruct-GGUF",
            "Phi-4-mini-instruct-Q4_K_M.gguf",
            2_394_000_000, 131072, "synthesis"),

        // Alternatives for benchmarking
        new("qwen2.5-3b-instruct", "Qwen 2.5 3B Instruct",
            "Qwen/Qwen2.5-3B-Instruct-GGUF",
            "qwen2.5-3b-instruct-q4_k_m.gguf",
            2_000_000_000, 32768, "synthesis"),

        new("gemma-3-1b-it", "Gemma 3 1B IT",
            "unsloth/gemma-3-1b-it-GGUF",
            "gemma-3-1b-it-Q4_K_M.gguf",
            700_000_000, 32768, "sentinel")
    ];

    /// <summary>
    ///     Get the default sentinel model info.
    /// </summary>
    public static ModelInfo GetSentinel(string name) =>
        Models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Role == "sentinel")
        ?? Models.First(m => m.Role == "sentinel");

    /// <summary>
    ///     Get the default synthesis model info.
    /// </summary>
    public static ModelInfo GetSynthesis(string name) =>
        Models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Role == "synthesis")
        ?? Models.First(m => m.Role == "synthesis");

    /// <summary>
    ///     Get all models for a given role.
    /// </summary>
    public static IEnumerable<ModelInfo> GetModelsForRole(string role) =>
        Models.Where(m => m.Role.Equals(role, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Find a model by name (any role).
    /// </summary>
    public static ModelInfo? FindByName(string name) =>
        Models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Build the HuggingFace download URL for a model.
    /// </summary>
    public static string GetDownloadUrl(ModelInfo model) =>
        $"https://huggingface.co/{model.HuggingFaceRepo}/resolve/main/{model.Filename}";
}

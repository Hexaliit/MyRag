namespace LucidSupport.Models;

/// <summary>
///     Configuration for the LucidSupport runtime.
/// </summary>
public sealed record LucidSupportConfig
{
    /// <summary>Directory containing .support.md files.</summary>
    public required string SupportDirectory { get; init; }

    /// <summary>Ollama API base URL for AI feedback.</summary>
    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";

    /// <summary>Small sentinel model for AI feedback.</summary>
    public string SentinelModel { get; init; } = "qwen3:0.6b";

    /// <summary>Maximum tokens for sentinel model output.</summary>
    public int SentinelMaxTokens { get; init; } = 256;

    /// <summary>Whether AI-powered feedback is enabled.</summary>
    public bool EnableAiFeedback { get; init; }

    /// <summary>Whether pattern-based field guidance is enabled.</summary>
    public bool EnablePatternGuidance { get; init; } = true;

    /// <summary>Directory containing manual/knowledge files for ingestion.</summary>
    public string? ManualDirectory { get; init; }
}

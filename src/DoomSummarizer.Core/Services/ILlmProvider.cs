namespace DoomSummarizer.Services;

/// <summary>
/// Abstract LLM provider. Supports local (Ollama) and cloud (OpenAI, Anthropic) backends.
/// OllamaService delegates its HTTP calls through this interface.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Provider name for logging and budget tracking.</summary>
    string Name { get; }

    /// <summary>Check if the provider is available (can respond).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Generate a text completion.
    /// </summary>
    Task<string> GenerateAsync(LlmRequest request, CancellationToken ct = default);
}

/// <summary>
/// Unified request for all LLM providers.
/// Each provider maps this to its native API format.
/// </summary>
public record LlmRequest
{
    public required string Prompt { get; init; }
    public string? SystemPrompt { get; init; }
    public double Temperature { get; init; } = 0.4;
    public int MaxTokens { get; init; } = 4096;
    public bool JsonMode { get; init; }

    /// <summary>Role hint: "main", "sentinel", "analysis". Used by router for model selection.</summary>
    public string Role { get; init; } = "main";

    /// <summary>Convert to shared LlmOptions.</summary>
    public Mostlylucid.DocSummarizer.Services.LlmOptions ToOptions() => new()
    {
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        SystemPrompt = SystemPrompt,
        JsonMode = JsonMode,
        Role = Role
    };

    /// <summary>Create from shared LlmOptions.</summary>
    public static LlmRequest FromOptions(string prompt, Mostlylucid.DocSummarizer.Services.LlmOptions? options) => new()
    {
        Prompt = prompt,
        SystemPrompt = options?.SystemPrompt,
        Temperature = options?.Temperature ?? 0.4,
        MaxTokens = options?.MaxTokens ?? 4096,
        JsonMode = options?.JsonMode ?? false,
        Role = options?.Role ?? "main"
    };
}

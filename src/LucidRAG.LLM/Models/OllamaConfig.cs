namespace DoomSummarizer.Models;

/// <summary>
/// Ollama LLM backend configuration.
/// Shared between DoomSummarizer and LucidRAG.LLM for LlmRouter construction.
/// </summary>
public record OllamaConfig
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "gemma3:4b";
    public string SentinelModel { get; init; } = "qwen3:0.6b";
    public string EmbedModel { get; init; } = "nomic-embed-text";
    public double Temperature { get; init; } = 0.4;
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Context window size (tokens) for the main model. Used to budget evidence content.</summary>
    public int ContextSize { get; init; } = 8192;

    /// <summary>Context window size (tokens) for the sentinel model.</summary>
    public int SentinelContextSize { get; init; } = 32768;

    /// <summary>
    /// Compute max chars of evidence content per item for the given model context.
    /// Reserves space for prompt overhead (~300 tokens) and output (~500 tokens).
    /// Assumes ~3.5 chars per token for English text.
    /// </summary>
    public int MaxEvidenceCharsPerItem(bool sentinel, int itemCount)
    {
        var ctx = sentinel ? SentinelContextSize : ContextSize;
        var availableTokens = ctx - 800; // reserve for prompt + output
        var perItem = Math.Max(100, availableTokens / Math.Max(1, itemCount));
        return (int)(perItem * 3.5);
    }
}

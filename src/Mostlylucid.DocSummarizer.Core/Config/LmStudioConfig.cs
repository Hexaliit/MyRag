using System.Collections.Generic;

namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     LM Studio configuration
/// </summary>
public class LmStudioConfig
{
    /// <summary>
    ///     Base URL for LM Studio server
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:1234";

    /// <summary>
    ///     API Key (optional - LM Studio typically doesn't require one)
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Default chat model to use
    /// </summary>
    public string ChatModel { get; set; } = "";

    /// <summary>
    ///     Default embedding model to use
    /// </summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>
    ///     Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 1200;

    /// <summary>
    ///     Maximum retry attempts
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    ///     Initial retry delay in milliseconds
    /// </summary>
    public int InitialRetryDelayMs { get; set; } = 1000;

    /// <summary>
    ///     Maximum retry delay in milliseconds
    /// </summary>
    public int MaxRetryDelayMs { get; set; } = 30000;

    /// <summary>
    ///     Enable circuit breaker
    /// </summary>
    public bool EnableCircuitBreaker { get; set; } = true;

    /// <summary>
    ///     Failure ratio to open circuit (0.0-1.0)
    /// </summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>
    ///     Minimum throughput before circuit breaker evaluates
    /// </summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    /// <summary>
    ///     How long to keep circuit open before trying again (seconds)
    /// </summary>
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;

    /// <summary>
    ///     Temperature for chat completions
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    ///     Max tokens for chat completions
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    ///     Preferred embedding models (in order of preference)
    /// </summary>
    public List<string> PreferredEmbeddingModels { get; set; } = new()
    {
        "bge-m3",
        "multilingual-e5-large",
        "gte-multilingual",
        "jina-embeddings-v3",
        "nomic-embed-text",
        "mxbai-embed-large",
        "bge-large-en-v1.5",
        "e5-large-v2"
    };

    /// <summary>
    ///     Preferred chat models (in order of preference)
    /// </summary>
    public List<string> PreferredChatModels { get; set; } = new()
    {
        "qwen2.5-7b-instruct",
        "qwen2.5-14b-instruct",
        "llama-3.1-8b-instruct",
        "llama-3.1-70b-instruct",
        "mistral-nemo",
        "gemma-2-9b-it",
        "phi-3-medium-128k-instruct",
        "gemma-2-27b-it"
    };

    /// <summary>
    ///     Enable automatic model discovery on startup
    /// </summary>
    public bool AutoDiscoverModels { get; set; } = true;

    /// <summary>
    ///     Embedding context window (auto-detected if 0)
    /// </summary>
    public int EmbeddingContextWindow { get; set; } = 0;

    /// <summary>
    ///     Embedding dimension (auto-detected if 0)
    /// </summary>
    public int EmbeddingDimension { get; set; } = 0;

    /// <summary>
    ///     Default embedding model to use (for auto-discovery override)
    /// </summary>
    public string DefaultEmbeddingModel { get; set; } = "";
}
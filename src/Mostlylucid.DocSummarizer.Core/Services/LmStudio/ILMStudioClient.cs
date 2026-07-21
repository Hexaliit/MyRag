using System.Text;
using System.Text.Json;

namespace Mostlylucid.DocSummarizer.Services.LmStudio;

/// <summary>
///     LM Studio specific client interface.
///     Provides access to LM Studio's OpenAI-compatible REST API with additional features.
/// </summary>
public interface ILMStudioClient : ILlmClient, IEmbeddingClient
{
    /// <summary>
    ///     List all models currently loaded/available in LM Studio (detailed info)
    /// </summary>
    Task<LmStudioModelList> ListModelsDetailedAsync(CancellationToken ct = default);

    /// <summary>
    ///     Check if LM Studio server is running and healthy
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    /// <summary>
    ///     Get detailed info about a specific model
    /// </summary>
    Task<LmStudioModelInfo?> GetModelInfoAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    ///     Discover embedding-capable models automatically
    /// </summary>
    Task<IReadOnlyList<LmStudioEmbeddingModel>> DiscoverEmbeddingModelsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Discover chat/completion models automatically
    /// </summary>
    Task<IReadOnlyList<LmStudioChatModel>> DiscoverChatModelsAsync(CancellationToken ct = default);
}

/// <summary>
///     General LLM client interface for text generation.
///     Provider-agnostic - implemented by LM Studio, Ollama, OpenAI, Anthropic, etc.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    ///     Provider name for logging/diagnostics (e.g., "LM Studio", "Ollama", "OpenAI")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Generate text from a prompt
    /// </summary>
    Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default);

    /// <summary>
    ///     Stream text tokens from a prompt as they are generated.
    ///     True streaming (time-to-first-token) - yields each token/chunk as it arrives.
    /// </summary>
    IAsyncEnumerable<string> GenerateStreamingAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default);

    /// <summary>
    ///     Generate structured JSON output from a prompt
    /// </summary>
    Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default)
        where T : class;

    /// <summary>
    ///     Check if the LLM service is available and responding
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    ///     Get the context window size in tokens for the configured model
    /// </summary>
    Task<int> GetContextWindowAsync(CancellationToken ct = default);

    /// <summary>
    ///     List available models
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>
///     General embedding client interface.
///     Provider-agnostic - implemented by LM Studio, Ollama, OpenAI, Azure OpenAI, HuggingFace, ONNX, etc.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    ///     Provider name for logging/diagnostics
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Embedding dimension for this model
    /// </summary>
    int EmbeddingDimension { get; }

    /// <summary>
    ///     Model name being used for embeddings
    /// </summary>
    string ModelName { get; }

    /// <summary>
    ///     Initialize the service (download models if needed for local providers)
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    ///     Generate embedding for a single text
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    ///     Generate embeddings for multiple texts (batch)
    /// </summary>
    Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);

    /// <summary>
    ///     Get max context window (tokens) for the embedding model
    /// </summary>
    Task<int> GetContextWindowAsync(CancellationToken ct = default);
}

/// <summary>
///     Options for LLM generation requests
/// </summary>
public class LlmOptions
{
    /// <summary>
    ///     Override the default model for this request
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    ///     Temperature for generation (0.0-1.0). Lower = more deterministic.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    ///     Maximum tokens to generate
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    ///     System prompt/instructions
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    ///     Request JSON-formatted output
    /// </summary>
    public bool JsonMode { get; set; }

    /// <summary>
    ///     Role hint for model selection: "main", "sentinel", "analysis"
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    ///     Top-p sampling parameter
    /// </summary>
    public double? TopP { get; set; }

    /// <summary>
    ///     Frequency penalty
    /// </summary>
    public double? FrequencyPenalty { get; set; }

    /// <summary>
    ///     Presence penalty
    /// </summary>
    public double? PresencePenalty { get; set; }

    /// <summary>
    ///     Stop sequences
    /// </summary>
    public IReadOnlyList<string>? StopSequences { get; set; }

    /// <summary>
    ///     Default options with temperature 0.3
    /// </summary>
    public static LlmOptions Default => new() { Temperature = 0.3 };
}

/// <summary>
///     Message for chat completion (OpenAI-compatible format)
/// </summary>
public record ChatMessage
{
    /// <summary>Role: "system", "user", "assistant", "tool"</summary>
    public required string Role { get; init; }

    /// <summary>Message content</summary>
    public required string Content { get; init; }

    /// <summary>Optional name for the message</summary>
    public string? Name { get; init; }

    /// <summary>Tool calls (for assistant messages)</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Tool call ID (for tool messages)</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>
///     Tool call structure
/// </summary>
public record ToolCall
{
    public required string Id { get; init; }
    public required string Type { get; init; } // "function"
    public required FunctionCall Function { get; init; }
}

/// <summary>
///     Function call details
/// </summary>
public record FunctionCall
{
    public required string Name { get; init; }
    public required string Arguments { get; init; }
}

/// <summary>
///     Chat completion request
/// </summary>
public record ChatCompletionRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public double? TopP { get; init; }
    public double? FrequencyPenalty { get; init; }
    public double? PresencePenalty { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }
    public bool Stream { get; init; }
    public bool? JsonMode { get; init; }
    public IReadOnlyList<Tool>? Tools { get; init; }
    public string? ToolChoice { get; init; }
}

/// <summary>
///     Tool definition for function calling
/// </summary>
public record Tool
{
    public required string Type { get; init; } // "function"
    public required FunctionDefinition Function { get; init; }
}

/// <summary>
///     Function definition
/// </summary>
public record FunctionDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required JsonElement Parameters { get; init; }
}

/// <summary>
///     Chat completion response
/// </summary>
public record ChatCompletionResponse
{
    public string Id { get; init; } = "";
    public string Object { get; init; } = "chat.completion";
    public long Created { get; init; }
    public string Model { get; init; } = "";
    public required IReadOnlyList<Choice> Choices { get; init; }
    public Usage? Usage { get; init; }
}

/// <summary>
///     Choice in chat completion
/// </summary>
public record Choice
{
    public int Index { get; init; }
    public required ChatMessage Message { get; init; }
    public string? FinishReason { get; init; }
}

/// <summary>
///     Usage statistics
/// </summary>
public record Usage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}

/// <summary>
///     Streaming chat completion chunk
/// </summary>
public record ChatCompletionChunk
{
    public string Id { get; init; } = "";
    public string Object { get; init; } = "chat.completion.chunk";
    public long Created { get; init; }
    public string Model { get; init; } = "";
    public required IReadOnlyList<StreamingChoice> Choices { get; init; }
}

/// <summary>
///     Streaming choice
/// </summary>
public record StreamingChoice
{
    public int Index { get; init; }
    public required ChatMessageDelta Delta { get; init; }
    public string? FinishReason { get; init; }
}

/// <summary>
///     Delta for streaming
/// </summary>
public record ChatMessageDelta
{
    public string? Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
}

/// <summary>
///     Embedding request
/// </summary>
public record EmbeddingRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<string> Input { get; init; }
    public string? EncodingFormat { get; init; } = "float";
    public int? Dimensions { get; init; }
}

/// <summary>
///     Embedding response
/// </summary>
public record EmbeddingResponse
{
    public string Object { get; init; } = "list";
    public required IReadOnlyList<EmbeddingData> Data { get; init; }
    public string Model { get; init; } = "";
    public Usage? Usage { get; init; }
}

/// <summary>
///     Single embedding data
/// </summary>
public record EmbeddingData
{
    public int Index { get; init; }
    public string Object { get; init; } = "embedding";
    public required float[] Embedding { get; init; }
}

/// <summary>
///     LM Studio model list response
/// </summary>
public record LmStudioModelList
{
    public required IReadOnlyList<LmStudioModel> Data { get; init; }
}

/// <summary>
///     LM Studio model info
/// </summary>
public record LmStudioModel
{
    public required string Id { get; init; }
    public string Object { get; init; } = "model";
    public long Created { get; init; }
    public required string OwnedBy { get; init; }
    public LmStudioModelDetails? Details { get; init; }
}

/// <summary>
///     Detailed model info from LM Studio
/// </summary>
public record LmStudioModelDetails
{
    public string? Format { get; init; }
    public string? Family { get; init; }
    public string? ParameterSize { get; init; }
    public string? QuantizationLevel { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public int? ContextLength { get; init; }
    public string? Description { get; init; }
}

/// <summary>
///     Full model info response
/// </summary>
public record LmStudioModelInfo
{
    public required string Id { get; init; }
    public LmStudioModelDetails? Details { get; init; }
    public LmStudioModelCapabilities? Capabilities { get; init; }
}

/// <summary>
///     Model capabilities
/// </summary>
public record LmStudioModelCapabilities
{
    public bool SupportsChat { get; init; }
    public bool SupportsCompletion { get; init; }
    public bool SupportsEmbedding { get; init; }
    public bool SupportsTools { get; init; }
    public bool SupportsVision { get; init; }
    public int MaxContextLength { get; init; }
    public int? MaxOutputTokens { get; init; }
}

/// <summary>
///     Embedding model info
/// </summary>
public record LmStudioEmbeddingModel
{
    public required string Name { get; init; }
    public int Dimensions { get; init; }
    public int MaxContextLength { get; init; }
    public string? Description { get; init; }
    public string? Family { get; init; }
}

/// <summary>
///     Chat model info
/// </summary>
public record LmStudioChatModel
{
    public required string Name { get; init; }
    public int MaxContextLength { get; init; }
    public bool SupportsTools { get; init; }
    public bool SupportsVision { get; init; }
    public string? Family { get; init; }
    public string? Description { get; init; }
}
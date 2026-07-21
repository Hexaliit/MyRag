using System.Text.Json.Serialization;
using Mostlylucid.DocSummarizer.Services.LmStudio;

namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     Unified configuration for all embedding providers
/// </summary>
public class UnifiedEmbeddingConfig
{
    /// <summary>
    ///     Active embedding provider: "LMStudio", "OpenAI", "AzureOpenAI", "Ollama", "HuggingFace", "ONNX", "SentenceTransformers"
    /// </summary>
    public string Provider { get; set; } = "LMStudio";

    /// <summary>
    ///     Model name to use for embeddings
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    ///     LM Studio specific configuration
    /// </summary>
    public LmStudioEmbeddingConfig LmStudio { get; set; } = new();

    /// <summary>
    ///     OpenAI specific configuration
    /// </summary>
    public OpenAIEmbeddingConfig OpenAI { get; set; } = new();

    /// <summary>
    ///     Azure OpenAI specific configuration
    /// </summary>
    public AzureOpenAIEmbeddingConfig AzureOpenAI { get; set; } = new();

    /// <summary>
    ///     Ollama specific configuration
    /// </summary>
    public OllamaEmbeddingConfig Ollama { get; set; } = new();

    /// <summary>
    ///     HuggingFace specific configuration
    /// </summary>
    public HuggingFaceEmbeddingConfig HuggingFace { get; set; } = new();

    /// <summary>
    ///     ONNX Runtime specific configuration
    /// </summary>
    public OnnxEmbeddingConfig Onnx { get; set; } = new();

    /// <summary>
    ///     SentenceTransformers specific configuration
    /// </summary>
    public SentenceTransformersConfig SentenceTransformers { get; set; } = new();
}

/// <summary>
///     LM Studio embedding configuration
/// </summary>
public class LmStudioEmbeddingConfig
{
    /// <summary>
    ///     Default embedding model to use
    /// </summary>
    public string DefaultEmbeddingModel { get; set; } = "";

    /// <summary>
    ///     Preferred embedding models in order of preference (for auto-discovery)
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
    ///     Auto-discover embedding model if DefaultEmbeddingModel is empty
    /// </summary>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>
    ///     Batch size for embeddings
    /// </summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    ///     Max tokens per request
    /// </summary>
    public int MaxTokens { get; set; } = 8192;
}

/// <summary>
///     OpenAI embedding configuration
/// </summary>
public class OpenAIEmbeddingConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Model { get; set; } = "text-embedding-3-small";
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
///     Azure OpenAI embedding configuration
/// </summary>
public class AzureOpenAIEmbeddingConfig
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-02-01";
    public string Model { get; set; } = "text-embedding-3-small";
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
///     Ollama embedding configuration
/// </summary>
public class OllamaEmbeddingConfig
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "nomic-embed-text";
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
///     HuggingFace embedding configuration
/// </summary>
public class HuggingFaceEmbeddingConfig
{
    public string BaseUrl { get; set; } = "https://api-inference.huggingface.co";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "sentence-transformers/all-MiniLM-L6-v2";
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
///     ONNX Runtime embedding configuration
/// </summary>
public class OnnxEmbeddingConfig
{
    public string ModelName { get; set; } = "all-MiniLM-L6-v2";
    public string ExecutionProvider { get; set; } = "CPU"; // CPU, CUDA, DirectML, CoreML
    public string ModelPath { get; set; } = "";
    public string TokenizerPath { get; set; } = "";
    public int MaxLength { get; set; } = 512;
    public bool NormalizeEmbeddings { get; set; } = true;
    public int BatchSize { get; set; } = 32;
}

/// <summary>
///     SentenceTransformers (Python) embedding configuration
/// </summary>
public class SentenceTransformersConfig
{
    public string PythonPath { get; set; } = "python";
    public string ScriptPath { get; set; } = "";
    public string Model { get; set; } = "all-MiniLM-L6-v2";
    public string Device { get; set; } = "cpu"; // cpu, cuda, mps
    public int BatchSize { get; set; } = 32;
    public bool NormalizeEmbeddings { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
///     LLM Provider configuration
/// </summary>
public class LlmProviderConfig
{
    /// <summary>
    ///     Active LLM provider: "LMStudio", "OpenAI", "AzureOpenAI", "Ollama", "Anthropic", "GoogleAI", "OpenRouter"
    /// </summary>
    public string Provider { get; set; } = "LMStudio";

    /// <summary>
    ///     LM Studio LLM configuration
    /// </summary>
    public LmStudioConfig LmStudio { get; set; } = new();

    /// <summary>
    ///     OpenAI LLM configuration
    /// </summary>
    public OpenAILlmConfig OpenAI { get; set; } = new();

    /// <summary>
    ///     Azure OpenAI LLM configuration
    /// </summary>
    public AzureOpenAILlmConfig AzureOpenAI { get; set; } = new();

    /// <summary>
    ///     Ollama LLM configuration
    /// </summary>
    public OllamaConfig Ollama { get; set; } = new();

    /// <summary>
    ///     Anthropic LLM configuration
    /// </summary>
    public AnthropicConfig Anthropic { get; set; } = new();

    /// <summary>
    ///     Google AI (Gemini) configuration
    /// </summary>
    public GoogleAIConfig GoogleAI { get; set; } = new();

    /// <summary>
    ///     OpenRouter configuration
    /// </summary>
    public OpenRouterConfig OpenRouter { get; set; } = new();
}

/// <summary>
///     OpenAI LLM configuration
/// </summary>
public class OpenAILlmConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
///     Azure OpenAI LLM configuration
/// </summary>
public class AzureOpenAILlmConfig
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-02-01";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
///     Anthropic LLM configuration
/// </summary>
public class AnthropicConfig
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-3-5-haiku-latest";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
///     Google AI (Gemini) configuration
/// </summary>
public class GoogleAIConfig
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-1.5-flash";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 8192;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
///     OpenRouter configuration
/// </summary>
public class OpenRouterConfig
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "google/gemini-flash-1.5";
    public string? SiteUrl { get; set; }
    public string? SiteName { get; set; }
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
}
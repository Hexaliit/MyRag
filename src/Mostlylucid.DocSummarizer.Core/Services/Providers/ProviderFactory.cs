using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services.Embeddings;
using Mostlylucid.DocSummarizer.Services.LmStudio;
using Mostlylucid.DocSummarizer.Services.Providers;

namespace Mostlylucid.DocSummarizer.Services.Providers;

/// <summary>
///     Unified provider factory for LLM and embedding clients
/// </summary>
public interface IProviderFactory
{
    /// <summary>
    ///     Get LLM client for the configured provider
    /// </summary>
    ILlmClient GetLlmClient();

    /// <summary>
    ///     Get embedding client for the configured provider
    /// </summary>
    IEmbeddingClient GetEmbeddingClient();

    /// <summary>
    ///     Get LM Studio specific client (if LM Studio is configured)
    /// </summary>
    ILMStudioClient? GetLmStudioClient();

    /// <summary>
    ///     List all available providers
    /// </summary>
    IReadOnlyList<string> GetAvailableLlmProviders();

    IReadOnlyList<string> GetAvailableEmbeddingProviders();
}

/// <summary>
///     Provider factory implementation
/// </summary>
public class ProviderFactory : IProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProviderFactory> _logger;
    private readonly LlmProviderConfig _llmConfig;
    private readonly UnifiedEmbeddingConfig _embeddingConfig;

    public ProviderFactory(
        IServiceProvider serviceProvider,
        ILogger<ProviderFactory> logger,
        IOptions<LlmProviderConfig> llmConfig,
        IOptions<UnifiedEmbeddingConfig> embeddingConfig)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _llmConfig = llmConfig.Value;
        _embeddingConfig = embeddingConfig.Value;
    }

    public ILlmClient GetLlmClient()
    {
        var provider = _llmConfig.Provider ?? "LMStudio";
        _logger.LogInformation("Creating LLM client for provider: {Provider}", provider);

        return provider.ToLowerInvariant() switch
        {
            "lmstudio" => _serviceProvider.GetRequiredService<LMStudioLlmClient>(),
            "openai" => throw new NotSupportedException("OpenAI provider not yet implemented. Use LMStudio for now."),
            "azureopenai" => throw new NotSupportedException("Azure OpenAI provider not yet implemented. Use LMStudio for now."),
            "ollama" => throw new NotSupportedException("Ollama provider not yet implemented. Use LMStudio for now."),
            "anthropic" => throw new NotSupportedException("Anthropic provider not yet implemented. Use LMStudio for now."),
            "googleai" => throw new NotSupportedException("Google AI provider not yet implemented. Use LMStudio for now."),
            "openrouter" => throw new NotSupportedException("OpenRouter provider not yet implemented. Use LMStudio for now."),
            _ => throw new InvalidOperationException($"Unknown LLM provider: {provider}")
        };
    }

    public IEmbeddingClient GetEmbeddingClient()
    {
        var provider = _embeddingConfig.Provider ?? "LMStudio";
        _logger.LogInformation("Creating embedding client for provider: {Provider}", provider);

        return provider.ToLowerInvariant() switch
        {
            "lmstudio" => _serviceProvider.GetRequiredService<LmStudioEmbeddingClient>(),
            "openai" => throw new NotSupportedException("OpenAI embedding provider not yet implemented. Use LMStudio for now."),
            "azureopenai" => throw new NotSupportedException("Azure OpenAI embedding provider not yet implemented. Use LMStudio for now."),
            "ollama" => throw new NotSupportedException("Ollama embedding provider not yet implemented. Use LMStudio for now."),
            "huggingface" => throw new NotSupportedException("HuggingFace embedding provider not yet implemented. Use LMStudio for now."),
            "onnx" => throw new NotSupportedException("ONNX embedding provider not yet implemented. Use LMStudio for now."),
            "sentencetransformers" => throw new NotSupportedException("SentenceTransformers embedding provider not yet implemented. Use LMStudio for now."),
            _ => throw new InvalidOperationException($"Unknown embedding provider: {provider}")
        };
    }

    public ILMStudioClient? GetLmStudioClient()
    {
        return _llmConfig.Provider?.Equals("LMStudio", StringComparison.OrdinalIgnoreCase) == true
            ? _serviceProvider.GetService<ILMStudioClient>()
            : null;
    }

    public IReadOnlyList<string> GetAvailableLlmProviders() => new[]
    {
        "LMStudio"
    };

    public IReadOnlyList<string> GetAvailableEmbeddingProviders() => new[]
    {
        "LMStudio"
    };
}

/// <summary>
    ///     LM Studio LLM client wrapper - implements ILlmClient by delegating to ILMStudioClient
    /// </summary>
    public class LMStudioLlmClient : ILlmClient
    {
        private readonly ILMStudioClient _client;

        public LMStudioLlmClient(ILMStudioClient client) => _client = client;

        public string ProviderName => "LM Studio";

        public Task<string> GenerateAsync(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options = null, CancellationToken ct = default)
        {
            var lmStudioOptions = options != null ? ConvertToLmStudioOptions(options) : null;
            return _client.GenerateAsync(prompt, lmStudioOptions, ct);
        }

        public IAsyncEnumerable<string> GenerateStreamingAsync(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options = null, CancellationToken ct = default)
        {
            var lmStudioOptions = options != null ? ConvertToLmStudioOptions(options) : null;
            return _client.GenerateStreamingAsync(prompt, lmStudioOptions, ct);
        }

        public Task<T?> GenerateJsonAsync<T>(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options = null, CancellationToken ct = default)
            where T : class
        {
            var lmStudioOptions = options != null ? ConvertToLmStudioOptions(options) : null;
            return _client.GenerateJsonAsync<T>(prompt, lmStudioOptions, ct);
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => _client.IsAvailableAsync(ct);

        public Task<int> GetContextWindowAsync(CancellationToken ct = default)
            => ((ILlmClient)_client).GetContextWindowAsync(ct);

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
            => _client.ListModelsAsync(ct);

        private static Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions ConvertToLmStudioOptions(Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions options)
        {
            return new Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions
            {
                Model = options.Model,
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                SystemPrompt = options.SystemPrompt,
                JsonMode = options.JsonMode,
                Role = options.Role,
                TopP = options.TopP,
                FrequencyPenalty = options.FrequencyPenalty,
                PresencePenalty = options.PresencePenalty,
                StopSequences = options.StopSequences
            };
        }
    }

/// <summary>
///     LM Studio embedding client wrapper - implements IEmbeddingClient by delegating to ILMStudioClient
/// </summary>
public class LmStudioEmbeddingClient : IEmbeddingClient
{
    private readonly ILMStudioClient _client;

    public LmStudioEmbeddingClient(ILMStudioClient client) => _client = client;

    public string ProviderName => "LM Studio";
    public string ModelName => _client.ModelName;
    public int EmbeddingDimension => _client.EmbeddingDimension;

    public Task InitializeAsync(CancellationToken ct = default)
        => _client.InitializeAsync(ct);

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => _client.EmbedAsync(text, ct);

    public Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
        => _client.EmbedBatchAsync(texts, ct);

    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
        => ((IEmbeddingClient)_client).GetContextWindowAsync(ct);
}
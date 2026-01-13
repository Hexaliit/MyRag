using System.Collections.Concurrent;
using LucidRAG.LLM.Config;
using LucidRAG.LLM.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services;

/// <summary>
/// Factory for creating and caching named LLM providers.
/// </summary>
public class LlmProviderFactory : ILlmProviderFactory
{
    private readonly LlmProviderConfig _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPromptService _promptService;
    private readonly ILogger<LlmProviderFactory> _logger;
    private readonly ConcurrentDictionary<string, INamedLlmProvider> _providers = new();

    public LlmProviderFactory(
        IOptions<LlmProviderConfig> config,
        IServiceProvider serviceProvider,
        IPromptService promptService,
        ILogger<LlmProviderFactory> logger)
    {
        _config = config.Value;
        _serviceProvider = serviceProvider;
        _promptService = promptService;
        _logger = logger;

        InitializeProviders();
    }

    /// <inheritdoc />
    public INamedLlmProvider GetProvider(string name)
    {
        if (_providers.TryGetValue(name, out var provider))
            return provider;

        throw new KeyNotFoundException(
            $"LLM provider '{name}' not found. Available: {string.Join(", ", _providers.Keys)}");
    }

    /// <inheritdoc />
    public bool TryGetProvider(string name, out INamedLlmProvider? provider)
    {
        if (_providers.TryGetValue(name, out var p))
        {
            provider = p;
            return true;
        }

        provider = null;
        return false;
    }

    /// <inheritdoc />
    public INamedLlmProvider GetProviderForTier(ProviderTier tier)
    {
        var providerName = _config.Defaults.GetProviderForTier(tier);
        return GetProvider(providerName);
    }

    /// <inheritdoc />
    public INamedLlmProvider GetProviderForTier(string tierName)
    {
        var providerName = _config.Defaults.GetProviderForTier(tierName);
        return GetProvider(providerName);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetProviderNames() => _providers.Keys.ToList();

    /// <inheritdoc />
    public bool HasProvider(string name) => _providers.ContainsKey(name);

    /// <inheritdoc />
    public INamedLlmProvider GetDefault() => GetProviderForTier(ProviderTier.General);

    private void InitializeProviders()
    {
        foreach (var (name, providerConfig) in _config.Providers)
        {
            try
            {
                var provider = CreateProvider(name, providerConfig);
                if (provider != null)
                {
                    _providers[name] = provider;
                    _logger.LogInformation(
                        "Registered LLM provider: {Name} -> {Model} ({Backend})",
                        name, providerConfig.Model, provider.BackendType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create LLM provider: {Name}", name);
            }
        }

        _logger.LogInformation("Initialized {Count} LLM providers", _providers.Count);
    }

    private INamedLlmProvider? CreateProvider(string name, NamedProviderConfig providerConfig)
    {
        // Get model configuration
        if (!_config.Models.TryGetValue(providerConfig.Model, out var modelConfig))
        {
            _logger.LogWarning("Model '{Model}' not found for provider '{Name}'",
                providerConfig.Model, name);
            return null;
        }

        // Get backend configuration
        if (!_config.Backends.TryGetValue(modelConfig.Backend, out var backendConfig))
        {
            _logger.LogWarning("Backend '{Backend}' not found for model '{Model}'",
                modelConfig.Backend, providerConfig.Model);
            return null;
        }

        // Check if backend is enabled
        if (!backendConfig.Enabled)
        {
            _logger.LogDebug("Backend '{Backend}' is disabled, skipping provider '{Name}'",
                modelConfig.Backend, name);
            return null;
        }

        // Create the underlying ILlmService based on backend type
        var inner = CreateInnerService(backendConfig, modelConfig);
        if (inner == null)
        {
            _logger.LogWarning("Failed to create inner service for provider '{Name}'", name);
            return null;
        }

        // Wrap in named provider
        return CreateNamedProvider(name, inner, modelConfig, backendConfig);
    }

    private ILlmService? CreateInnerService(BackendConfig backendConfig, ModelConfig modelConfig)
    {
        var backendType = backendConfig.GetBackendType();

        return backendType switch
        {
            LlmBackendType.Ollama => CreateOllamaService(backendConfig, modelConfig),
            LlmBackendType.Anthropic => CreateAnthropicService(backendConfig, modelConfig),
            LlmBackendType.OpenAI or LlmBackendType.LMStudio => CreateOpenAIService(backendConfig, modelConfig),
            _ => null
        };
    }

    private ILlmService? CreateOllamaService(BackendConfig backendConfig, ModelConfig modelConfig)
    {
        try
        {
            // Create OllamaService and wrap in OllamaLlmService
            var ollamaConfig = new OllamaConfig
            {
                BaseUrl = backendConfig.BaseUrl,
                Model = modelConfig.Model,
                Temperature = modelConfig.Temperature,
                TimeoutSeconds = backendConfig.TimeoutSeconds
            };

            var ollamaService = new OllamaService(
                modelConfig.Model,
                "nomic-embed-text", // Default embedding model
                backendConfig.BaseUrl,
                TimeSpan.FromSeconds(backendConfig.TimeoutSeconds));

            return new OllamaLlmService(ollamaService, ollamaConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Ollama service");
            return null;
        }
    }

    private ILlmService? CreateAnthropicService(BackendConfig backendConfig, ModelConfig modelConfig)
    {
        try
        {
            // Try to get existing Anthropic service from DI
            var existingService = _serviceProvider.GetService<ILlmService>();
            if (existingService?.ProviderName.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) == true)
                return existingService;

            // Otherwise, we need the Anthropic assembly to create a new one
            _logger.LogDebug("Anthropic service not registered in DI, skipping");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Anthropic service");
            return null;
        }
    }

    private ILlmService? CreateOpenAIService(BackendConfig backendConfig, ModelConfig modelConfig)
    {
        try
        {
            // Try to get existing OpenAI service from DI
            var existingService = _serviceProvider.GetService<ILlmService>();
            if (existingService?.ProviderName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true)
                return existingService;

            // Otherwise, we need the OpenAI assembly to create a new one
            _logger.LogDebug("OpenAI service not registered in DI, skipping");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OpenAI service");
            return null;
        }
    }

    private INamedLlmProvider CreateNamedProvider(
        string name,
        ILlmService inner,
        ModelConfig modelConfig,
        BackendConfig backendConfig)
    {
        var backendType = backendConfig.GetBackendType();
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        return backendType switch
        {
            LlmBackendType.Ollama => new OllamaProvider(
                name, inner, _promptService, modelConfig, backendConfig,
                loggerFactory.CreateLogger<OllamaProvider>()),

            LlmBackendType.Anthropic => new AnthropicProvider(
                name, inner, _promptService, modelConfig, backendConfig,
                loggerFactory.CreateLogger<AnthropicProvider>()),

            LlmBackendType.OpenAI => new OpenAIProvider(
                name, inner, _promptService, modelConfig, backendConfig,
                loggerFactory.CreateLogger<OpenAIProvider>()),

            LlmBackendType.LMStudio => new OpenAIProvider(
                name, inner, _promptService, modelConfig, backendConfig,
                loggerFactory.CreateLogger<OpenAIProvider>(),
                isLmStudio: true),

            _ => throw new NotSupportedException($"Backend type '{backendType}' not supported")
        };
    }
}

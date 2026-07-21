using System.Collections.Concurrent;
using LucidRAG.LLM.Config;
using LucidRAG.LLM.Services.LoadBalancing;
using LucidRAG.LLM.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using LLMProviderConfig = LucidRAG.LLM.Config.LlmProviderConfig;

namespace LucidRAG.LLM.Services;

/// <summary>
///     Factory for creating and caching named LLM providers.
/// </summary>
public class LlmProviderFactory : ILlmProviderFactory
{
    private readonly LLMProviderConfig _config;
    private readonly ILogger<LlmProviderFactory> _logger;
    private readonly IPromptService _promptService;
    private readonly ConcurrentDictionary<string, INamedLlmProvider> _providers = new();
    private readonly IServiceProvider _serviceProvider;

    public LlmProviderFactory(
        IOptions<LLMProviderConfig> config,
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
    public IReadOnlyList<string> GetProviderNames()
    {
        return _providers.Keys.ToList();
    }

    /// <inheritdoc />
    public bool HasProvider(string name)
    {
        return _providers.ContainsKey(name);
    }

    /// <inheritdoc />
    public INamedLlmProvider GetDefault()
    {
        return GetProviderForTier(ProviderTier.General);
    }

    private void InitializeProviders()
    {
        foreach (var (name, providerConfig) in _config.Providers)
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
            LlmBackendType.LLamaSharp => CreateLLamaSharpService(),
            _ => null
        };
    }

    private ILlmService? CreateOllamaService(BackendConfig backendConfig, ModelConfig modelConfig)
    {
        try
        {
            var effectiveEndpoints = GetFilteredEndpoints(backendConfig, modelConfig);

            if (effectiveEndpoints.Count == 0)
            {
                _logger.LogWarning("No endpoints available for Ollama backend");
                return null;
            }

            // Single endpoint: direct creation (zero overhead)
            if (effectiveEndpoints.Count == 1)
                return CreateSingleOllamaService(effectiveEndpoints[0].Url, backendConfig, modelConfig);

            // Multi-endpoint: create per-endpoint services and wrap in load balancer
            var endpoints = new List<EndpointState>();
            var services = new Dictionary<string, ILlmService>();

            foreach (var entry in effectiveEndpoints)
            {
                var service = CreateSingleOllamaService(entry.Url, backendConfig, modelConfig);
                if (service == null) continue;

                endpoints.Add(new EndpointState(entry.Url, entry.Name));
                services[entry.Url] = service;
            }

            return WrapInLoadBalancer("Ollama", modelConfig.Backend, endpoints, services, backendConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Ollama service");
            return null;
        }
    }

    private ILlmService CreateSingleOllamaService(string url, BackendConfig backendConfig, ModelConfig modelConfig)
    {
        var ollamaConfig = new OllamaConfig
        {
            BaseUrl = url,
            Model = modelConfig.Model,
            Temperature = modelConfig.Temperature,
            TimeoutSeconds = backendConfig.TimeoutSeconds
        };

        var ollamaService = new OllamaService(
            modelConfig.Model,
            "nomic-embed-text",
            url,
            TimeSpan.FromSeconds(backendConfig.TimeoutSeconds));

        return new OllamaLlmService(ollamaService, ollamaConfig);
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
            var effectiveEndpoints = GetFilteredEndpoints(backendConfig, modelConfig);

            // Multi-endpoint for LMStudio-type backends
            if (effectiveEndpoints.Count > 1 && backendConfig.GetBackendType() == LlmBackendType.LMStudio)
            {
                var endpoints = new List<EndpointState>();
                var services = new Dictionary<string, ILlmService>();

                foreach (var entry in effectiveEndpoints)
                {
                    // Create a per-endpoint OllamaService as OpenAI-compatible proxy
                    // LMStudio uses OpenAI-compatible API, which OllamaService can also target
                    var service = CreateSingleOllamaService(entry.Url, backendConfig, modelConfig);
                    if (service == null) continue;

                    endpoints.Add(new EndpointState(entry.Url, entry.Name));
                    services[entry.Url] = service;
                }

                var balanced = WrapInLoadBalancer("LMStudio", modelConfig.Backend, endpoints, services, backendConfig);
                if (balanced != null)
                    return balanced;
            }

            // Single endpoint or non-LMStudio: use DI-registered service
            var existingService = _serviceProvider.GetService<ILlmService>();
            if (existingService?.ProviderName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true)
                return existingService;

            _logger.LogDebug("OpenAI service not registered in DI, skipping");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OpenAI service");
            return null;
        }
    }

    private ILlmService? CreateLLamaSharpService()
    {
        try
        {
            // Resolve from DI — the LLamaSharp assembly registers its service there
            var existingService = _serviceProvider.GetService<ILlmService>();
            if (existingService?.ProviderName.Contains("LLamaSharp", StringComparison.OrdinalIgnoreCase) == true)
                return existingService;

            _logger.LogDebug("LLamaSharp service not registered in DI, skipping");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create LLamaSharp service");
            return null;
        }
    }

    /// <summary>
    ///     Get effective endpoints for a backend, filtered by model-level endpoint restrictions.
    /// </summary>
    private static List<EndpointEntry> GetFilteredEndpoints(BackendConfig backendConfig, ModelConfig modelConfig)
    {
        var endpoints = backendConfig.GetEffectiveEndpoints();

        if (modelConfig.Endpoints is { Count: > 0 })
        {
            var allowed = new HashSet<string>(modelConfig.Endpoints, StringComparer.OrdinalIgnoreCase);
            endpoints = endpoints.Where(e => allowed.Contains(e.Url)).ToList();
        }

        return endpoints;
    }

    private ILlmService? WrapInLoadBalancer(
        string label,
        string backendName,
        List<EndpointState> endpoints,
        Dictionary<string, ILlmService> services,
        BackendConfig backendConfig)
    {
        if (endpoints.Count == 0) return null;
        if (endpoints.Count == 1) return services.Values.First();

        var selector = CreateSelector(backendConfig.GetSelectionStrategy());
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        _logger.LogInformation(
            "Created load-balanced {Label} service with {Count} endpoints ({Strategy})",
            label, endpoints.Count, backendConfig.Selection);

        return new LoadBalancedLlmService(
            backendName,
            endpoints,
            services,
            selector,
            loggerFactory.CreateLogger<LoadBalancedLlmService>(),
            backendConfig.HealthCheckIntervalSeconds);
    }

    private static IEndpointSelector CreateSelector(EndpointSelectionStrategy strategy)
    {
        return strategy switch
        {
            EndpointSelectionStrategy.Fastest => new FastestSelector(),
            _ => new RoundRobinSelector()
        };
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
                true),

            LlmBackendType.LLamaSharp => new LLamaSharpProvider(
                name, inner, _promptService, modelConfig, backendConfig,
                loggerFactory.CreateLogger<LLamaSharpProvider>()),

            _ => throw new NotSupportedException($"Backend type '{backendType}' not supported")
        };
    }
}
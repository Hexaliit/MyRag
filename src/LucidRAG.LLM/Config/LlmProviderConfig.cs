using YamlDotNet.Serialization;

namespace LucidRAG.LLM.Config;

/// <summary>
/// Root configuration for LLM providers, loaded from llm-providers.yaml.
/// </summary>
public class LlmProviderConfig
{
    public const string SectionName = "LlmProviders";

    /// <summary>
    /// Backend connection definitions (e.g., ollama-local, anthropic, openai).
    /// </summary>
    public Dictionary<string, BackendConfig> Backends { get; set; } = new();

    /// <summary>
    /// Model registry - define models once, reference by name.
    /// </summary>
    public Dictionary<string, ModelConfig> Models { get; set; } = new();

    /// <summary>
    /// Named provider aliases (e.g., fast-local, general, smart, vision).
    /// </summary>
    public Dictionary<string, NamedProviderConfig> Providers { get; set; } = new();

    /// <summary>
    /// Default provider assignments by tier.
    /// </summary>
    public DefaultsConfig Defaults { get; set; } = new();
}

/// <summary>
/// Backend connection configuration.
/// </summary>
public class BackendConfig
{
    /// <summary>
    /// Backend type: ollama, anthropic, openai.
    /// LMStudio uses type "openai" with a local base_url.
    /// </summary>
    public string Type { get; set; } = "ollama";

    /// <summary>
    /// Base URL for the API endpoint.
    /// </summary>
    [YamlMember(Alias = "base_url")]
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// API key (supports ${ENV_VAR} syntax for environment variables).
    /// </summary>
    [YamlMember(Alias = "api_key")]
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// API version header (Anthropic-specific).
    /// </summary>
    [YamlMember(Alias = "api_version")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    [YamlMember(Alias = "timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether this backend is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    #region Multi-Endpoint Load Balancing

    /// <summary>
    /// Multiple endpoint URLs for load balancing. When set, replaces base_url.
    /// </summary>
    public List<EndpointEntry>? Endpoints { get; set; }

    /// <summary>
    /// Endpoint selection strategy: round_robin or fastest.
    /// </summary>
    public string Selection { get; set; } = "round_robin";

    /// <summary>
    /// Interval in seconds between health probes for unhealthy endpoints.
    /// </summary>
    [YamlMember(Alias = "health_check_interval_seconds")]
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Whether this backend has multiple endpoints configured.
    /// </summary>
    public bool IsMultiEndpoint => Endpoints is { Count: > 1 };

    /// <summary>
    /// Parse the selection strategy string to enum.
    /// </summary>
    public EndpointSelectionStrategy GetSelectionStrategy() => Selection?.ToLowerInvariant() switch
    {
        "fastest" => EndpointSelectionStrategy.Fastest,
        _ => EndpointSelectionStrategy.RoundRobin
    };

    /// <summary>
    /// Get effective endpoints: returns Endpoints if set, otherwise wraps BaseUrl in a single-element list.
    /// </summary>
    public List<EndpointEntry> GetEffectiveEndpoints()
    {
        if (Endpoints is { Count: > 0 })
            return Endpoints;

        if (!string.IsNullOrEmpty(BaseUrl))
            return [new EndpointEntry { Url = BaseUrl }];

        return [];
    }

    #endregion

    #region Resilience Configuration (Polly)

    /// <summary>
    /// Maximum retry attempts for failed requests.
    /// </summary>
    [YamlMember(Alias = "max_retries")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial delay in milliseconds before first retry.
    /// </summary>
    [YamlMember(Alias = "initial_retry_delay_ms")]
    public int InitialRetryDelayMs { get; set; } = 500;

    /// <summary>
    /// Maximum delay in milliseconds between retries.
    /// </summary>
    [YamlMember(Alias = "max_retry_delay_ms")]
    public int MaxRetryDelayMs { get; set; } = 30000;

    /// <summary>
    /// Minimum number of requests before circuit breaker can trip.
    /// </summary>
    [YamlMember(Alias = "circuit_breaker_threshold")]
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    /// Duration in seconds the circuit breaker stays open.
    /// </summary>
    [YamlMember(Alias = "circuit_breaker_duration_seconds")]
    public int CircuitBreakerDurationSeconds { get; set; } = 30;

    #endregion

    /// <summary>
    /// Resolve API key from environment variable if needed.
    /// </summary>
    public string ResolveApiKey()
    {
        if (string.IsNullOrEmpty(ApiKey))
            return "";

        if (ApiKey.StartsWith("${") && ApiKey.EndsWith("}"))
        {
            var envVar = ApiKey[2..^1];
            return Environment.GetEnvironmentVariable(envVar) ?? "";
        }

        return ApiKey;
    }

    /// <summary>
    /// Parse backend type from string.
    /// </summary>
    public LlmBackendType GetBackendType() => Type.ToLowerInvariant() switch
    {
        "ollama" => LlmBackendType.Ollama,
        "anthropic" => LlmBackendType.Anthropic,
        "openai" => LlmBackendType.OpenAI,
        "lmstudio" => LlmBackendType.LMStudio,
        _ => LlmBackendType.Ollama
    };
}

/// <summary>
/// Model configuration.
/// </summary>
public class ModelConfig
{
    /// <summary>
    /// Reference to backends key.
    /// </summary>
    public string Backend { get; set; } = "";

    /// <summary>
    /// Model identifier (e.g., "claude-3-5-sonnet-latest", "gpt-4o", "llama3.2:3b").
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Context window size in tokens.
    /// </summary>
    [YamlMember(Alias = "context_window")]
    public int ContextWindow { get; set; } = 8192;

    /// <summary>
    /// Maximum tokens to generate.
    /// </summary>
    [YamlMember(Alias = "max_tokens")]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Default temperature (0.0-1.0).
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// Model capabilities (e.g., "vision", "function_calling").
    /// </summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>
    /// Check if model supports vision.
    /// </summary>
    public bool SupportsVision => Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional endpoint URL filter: restrict this model to a subset of the backend's endpoints.
    /// URLs must match entries in the backend's endpoints list.
    /// </summary>
    public List<string>? Endpoints { get; set; }
}

/// <summary>
/// Named provider configuration (semantic alias).
/// </summary>
public class NamedProviderConfig
{
    /// <summary>
    /// Reference to models key.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Fallback model if primary fails.
    /// </summary>
    public string? Fallback { get; set; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Default tier assignments.
/// </summary>
public class DefaultsConfig
{
    /// <summary>
    /// Provider for triage/classification (fast local).
    /// </summary>
    public string Triage { get; set; } = "fast-local";

    /// <summary>
    /// Provider for general tasks.
    /// </summary>
    public string General { get; set; } = "general";

    /// <summary>
    /// Provider for complex synthesis.
    /// </summary>
    public string Synthesis { get; set; } = "smart";

    /// <summary>
    /// Provider for vision/image tasks.
    /// </summary>
    public string Vision { get; set; } = "vision";

    /// <summary>
    /// Fallback provider.
    /// </summary>
    public string Fallback { get; set; } = "budget-cloud";

    /// <summary>
    /// Get provider name for tier.
    /// </summary>
    public string GetProviderForTier(ProviderTier tier) => tier switch
    {
        ProviderTier.Triage => Triage,
        ProviderTier.General => General,
        ProviderTier.Synthesis => Synthesis,
        ProviderTier.Vision => Vision,
        ProviderTier.Fallback => Fallback,
        _ => General
    };

    /// <summary>
    /// Get provider name for tier string.
    /// </summary>
    public string GetProviderForTier(string tier) => tier.ToLowerInvariant() switch
    {
        "triage" => Triage,
        "general" => General,
        "synthesis" => Synthesis,
        "vision" => Vision,
        "fallback" => Fallback,
        _ => General
    };
}

/// <summary>
/// A single endpoint in a multi-endpoint backend configuration.
/// </summary>
public class EndpointEntry
{
    /// <summary>
    /// Endpoint URL (e.g., http://localhost:11434).
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Optional human-readable label for this endpoint.
    /// </summary>
    public string? Name { get; set; }
}

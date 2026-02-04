using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Resilience;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Service bag injected into plugins during initialization.
///     Provides shared infrastructure without coupling to the host application.
/// </summary>
public record SourcePluginServices
{
    /// <summary>Shared HTTP client for network requests.</summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>API key management.</summary>
    public required ApiKeyService ApiKeys { get; init; }

    /// <summary>Budget enforcement for paid APIs.</summary>
    public required IApiBudget ApiBudget { get; init; }

    /// <summary>Circuit breaker for failing services.</summary>
    public required ICircuitBreaker CircuitBreaker { get; init; }
}
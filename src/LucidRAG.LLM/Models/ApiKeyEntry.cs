namespace DoomSummarizer.Models;

/// <summary>
/// A single API service definition.
/// Each entry is self-contained: name, key, service-specific config, enabled flag, and budget.
/// API keys loaded from config JSON, user secrets ("keys:0:GoogleSearch"), or env vars (DOOM_GOOGLE_SEARCH).
/// </summary>
public record ApiKeyEntry
{
    /// <summary>Service identifier: "google_search", "google_places".</summary>
    public string Name { get; init; } = "";

    /// <summary>API key. Prefer user-secrets or env vars over storing in plain text.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Service-specific extra ID (e.g., Google Custom Search Engine ID).</summary>
    public string? SearchEngineId { get; init; }

    /// <summary>Whether this service is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Max requests per day for this service. 0 = use global limit.</summary>
    public int MaxRequestsPerDay { get; init; } = 100;

    /// <summary>Lifetime request cap. 0 = unlimited.</summary>
    public int MaxRequests { get; init; } = 0;

    /// <summary>Daily budget in USD for this service. 0 = use global budget.</summary>
    public double DailyBudgetUsd { get; init; } = 0;

    /// <summary>Estimated cost per API call in USD.</summary>
    public double CostPerRequest { get; init; } = 0.005;

    /// <summary>Context window size (tokens) for the main model. Used for evidence budgeting.</summary>
    public int ContextSize { get; init; } = 0;

    /// <summary>Context window size (tokens) for the sentinel model.</summary>
    public int SentinelContextSize { get; init; } = 0;

    /// <summary>Minimum delay (ms) between API requests. Prevents rate limiting on free tiers.</summary>
    public int RateLimitMs { get; init; } = 200;

    /// <summary>Max retry attempts on 429/5xx errors (0 = no retry).</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Consecutive failures before circuit breaker opens. 0 = disabled.</summary>
    public int CircuitBreakerThreshold { get; init; } = 3;

    /// <summary>Seconds before circuit breaker resets to half-open.</summary>
    public int CircuitBreakerResetSeconds { get; init; } = 60;
}

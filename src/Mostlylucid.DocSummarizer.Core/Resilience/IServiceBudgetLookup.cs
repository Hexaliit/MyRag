namespace Mostlylucid.DocSummarizer.Resilience;

/// <summary>
/// Provides per-service budget configuration for the budget tracker.
/// Implementations map from their domain-specific config (e.g., ApiKeyEntry) to this shared type.
/// </summary>
public interface IServiceBudgetLookup
{
    /// <summary>
    /// Get budget info for a named service. Returns null if the service is unknown.
    /// </summary>
    ServiceBudgetInfo? GetServiceBudgetInfo(string service);
}

/// <summary>
/// Budget-relevant configuration for a single API service.
/// </summary>
public record ServiceBudgetInfo
{
    /// <summary>Whether this service is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Max requests per day for this service. 0 = use global limit.</summary>
    public int MaxRequestsPerDay { get; init; }

    /// <summary>Lifetime request cap. 0 = unlimited.</summary>
    public int MaxRequests { get; init; }

    /// <summary>Daily budget in USD for this service. 0 = use global budget.</summary>
    public double DailyBudgetUsd { get; init; }

    /// <summary>Estimated cost per API call in USD.</summary>
    public double CostPerRequest { get; init; } = 0.005;
}

namespace Mostlylucid.DocSummarizer.Resilience;

/// <summary>
/// Global budget controls across all paid/limited APIs.
/// Individual service limits (via <see cref="IServiceBudgetLookup"/>) override these when set.
/// </summary>
public record ApiBudgetConfig
{
    /// <summary>Global daily request limit across all paid APIs. 0 = unlimited.</summary>
    public int GlobalMaxRequestsPerDay { get; init; } = 200;

    /// <summary>Global daily budget in USD. 0 = unlimited.</summary>
    public double GlobalDailyBudgetUsd { get; init; } = 2.0;
}

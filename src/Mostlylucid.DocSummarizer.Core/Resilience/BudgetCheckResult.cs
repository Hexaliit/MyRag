namespace Mostlylucid.DocSummarizer.Resilience;

/// <summary>
/// Result of a pre-flight budget check for an API call.
/// </summary>
public record BudgetCheckResult
{
    /// <summary>Whether the request is allowed.</summary>
    public bool IsAllowed { get; init; }

    /// <summary>Reason for denial (null if allowed).</summary>
    public string? DenialReason { get; init; }

    /// <summary>Number of requests made today for this service.</summary>
    public int DailyCount { get; init; }

    /// <summary>Maximum requests allowed today.</summary>
    public int DailyLimit { get; init; }

    /// <summary>Cost per request in USD.</summary>
    public double CostPerRequest { get; init; }

    public static BudgetCheckResult Allowed(int dailyCount = 0, int dailyLimit = 0, double costPerRequest = 0)
        => new()
        {
            IsAllowed = true,
            DailyCount = dailyCount,
            DailyLimit = dailyLimit,
            CostPerRequest = costPerRequest
        };

    public static BudgetCheckResult Denied(string reason)
        => new() { IsAllowed = false, DenialReason = reason };
}

namespace Mostlylucid.DocSummarizer.Resilience;

/// <summary>
/// Interface for API budget enforcement.
/// Tracks per-service and global request counts and cost budgets.
/// </summary>
public interface IApiBudget
{
    /// <summary>
    /// Pre-flight check: is this service allowed to make a request?
    /// Does NOT record usage — call <see cref="RecordUsageAsync"/> after a successful request.
    /// </summary>
    Task<BudgetCheckResult> CheckBudgetAsync(string service);

    /// <summary>
    /// Record a completed request for budget tracking.
    /// Call this after a successful API call.
    /// </summary>
    Task RecordUsageAsync(string service, int count = 1);
}

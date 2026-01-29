namespace Mostlylucid.DocSummarizer.Resilience;

/// <summary>
/// Interface for persistent circuit breaker with failure-type-aware retry semantics.
/// Implementors track service health across restarts and enforce backoff strategies.
/// </summary>
public interface ICircuitBreaker
{
    /// <summary>
    /// Fast synchronous check if the circuit is open (service should NOT be used).
    /// Uses in-memory cache — no I/O.
    /// </summary>
    bool IsCircuitOpen(string service);

    /// <summary>
    /// Full async check including retry-after evaluation and half-open probing.
    /// Returns true if the service is available for requests.
    /// </summary>
    Task<bool> IsServiceAvailableAsync(string service);

    /// <summary>
    /// Report a successful request — closes circuit, resets failure count.
    /// </summary>
    Task ReportSuccessAsync(string service);

    /// <summary>
    /// Report a failed request — may trip circuit based on failure type and threshold.
    /// </summary>
    Task ReportFailureAsync(string service, CircuitFailureType failureType, string? reason = null);

    /// <summary>
    /// Directly trip a circuit (e.g., on budget denial).
    /// </summary>
    Task TripCircuitAsync(string service, CircuitFailureType failureType, string? reason = null);

    /// <summary>
    /// Get current circuit entry for a service (null if not tracked).
    /// </summary>
    CircuitEntry? GetCircuitEntry(string service);

    /// <summary>
    /// Classify an HTTP status code to a failure type.
    /// </summary>
    static CircuitFailureType ClassifyHttpStatus(int statusCode) => statusCode switch
    {
        429 => CircuitFailureType.RateLimit,
        401 or 403 => CircuitFailureType.AuthError,
        >= 500 and < 600 => CircuitFailureType.ServerError,
        _ => CircuitFailureType.None
    };

    /// <summary>
    /// Classify a budget denial reason to a failure type.
    /// </summary>
    static CircuitFailureType ClassifyBudgetDenial(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return CircuitFailureType.None;
        if (reason.Contains("daily", StringComparison.OrdinalIgnoreCase)) return CircuitFailureType.DailyLimit;
        if (reason.Contains("lifetime", StringComparison.OrdinalIgnoreCase)) return CircuitFailureType.LifetimeLimit;
        if (reason.Contains("budget", StringComparison.OrdinalIgnoreCase)) return CircuitFailureType.BudgetExhausted;
        if (reason.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return CircuitFailureType.AuthError;
        return CircuitFailureType.BudgetExhausted;
    }
}

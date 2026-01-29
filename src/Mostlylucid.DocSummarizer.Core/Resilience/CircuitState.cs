namespace Mostlylucid.DocSummarizer.Resilience;

/// <summary>
/// Circuit breaker state machine status.
/// </summary>
public enum CircuitStatus
{
    /// <summary>Normal operation — requests pass through.</summary>
    Closed,

    /// <summary>Failing — reject requests until retry_after.</summary>
    Open,

    /// <summary>Probing — allow ONE request to test recovery.</summary>
    HalfOpen
}

/// <summary>
/// Classification of failure that caused a circuit to trip.
/// Different failure types have different retry strategies.
/// </summary>
public enum CircuitFailureType
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Budget exhausted for today → retry at midnight UTC.</summary>
    DailyLimit,

    /// <summary>Lifetime cap reached → never retry automatically.</summary>
    LifetimeLimit,

    /// <summary>429 Too Many Requests → exponential backoff.</summary>
    RateLimit,

    /// <summary>5xx errors → shorter exponential backoff.</summary>
    ServerError,

    /// <summary>Cost budget exhausted → retry at midnight UTC.</summary>
    BudgetExhausted,

    /// <summary>401/403 → never retry (bad key).</summary>
    AuthError
}

/// <summary>
/// Snapshot of a service's circuit breaker state.
/// </summary>
public record CircuitEntry
{
    public string Service { get; init; } = "";
    public CircuitStatus Status { get; init; } = CircuitStatus.Closed;
    public CircuitFailureType FailureType { get; init; } = CircuitFailureType.None;
    public int FailureCount { get; init; }
    public DateTimeOffset TrippedAt { get; init; }
    public DateTimeOffset RetryAfter { get; init; }
    public string? LastFailureReason { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

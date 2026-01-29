namespace DoomSummarizer.Models;

public enum CircuitStatus
{
    Closed,   // Normal operation
    Open,     // Failing — reject requests until retry_after
    HalfOpen  // Probing — allow ONE request to test recovery
}

public enum CircuitFailureType
{
    None,
    DailyLimit,      // Budget exhausted for today → retry at midnight UTC
    LifetimeLimit,   // Lifetime cap reached → never retry automatically
    RateLimit,       // 429 Too Many Requests → exponential backoff
    ServerError,     // 5xx errors → shorter exponential backoff
    BudgetExhausted, // Cost budget exhausted → retry at midnight UTC
    AuthError        // 401/403 → never retry (bad key)
}

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

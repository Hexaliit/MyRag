namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
///     Thread-safe per-endpoint health and latency tracking.
///     Uses lock-free operations (Interlocked/Volatile) for high-throughput scenarios.
/// </summary>
public sealed class EndpointState
{
    private const double EmaAlpha = 0.3;
    private const int MaxConsecutiveFailures = 3;
    private int _consecutiveFailures;
    private double _emaResponseTimeMs = double.MaxValue;
    private long _totalFailures;

    private long _totalRequests;

    public EndpointState(string url, string? name = null)
    {
        Url = url;
        Name = name ?? url;
    }

    /// <summary>Endpoint URL.</summary>
    public string Url { get; }

    /// <summary>Human-readable label.</summary>
    public string Name { get; }

    /// <summary>EMA-smoothed response time in milliseconds. MaxValue when no data.</summary>
    public double EmaResponseTimeMs => Volatile.Read(ref _emaResponseTimeMs);

    /// <summary>Total requests routed to this endpoint.</summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>Total failed requests.</summary>
    public long TotalFailures => Interlocked.Read(ref _totalFailures);

    /// <summary>Current consecutive failure count.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>Endpoint is healthy when consecutive failures are below threshold.</summary>
    public bool IsHealthy => Volatile.Read(ref _consecutiveFailures) < MaxConsecutiveFailures;

    /// <summary>Record a successful request and update EMA response time.</summary>
    public void RecordSuccess(double responseTimeMs)
    {
        Interlocked.Increment(ref _totalRequests);
        Volatile.Write(ref _consecutiveFailures, 0);

        // Update EMA: lock-free compare-and-swap loop
        double oldEma, newEma;
        do
        {
            oldEma = Volatile.Read(ref _emaResponseTimeMs);
            newEma = oldEma >= double.MaxValue
                ? responseTimeMs
                : EmaAlpha * responseTimeMs + (1 - EmaAlpha) * oldEma;
        } while (Interlocked.CompareExchange(ref _emaResponseTimeMs, newEma, oldEma) != oldEma);
    }

    /// <summary>Record a failed request.</summary>
    public void RecordFailure()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _totalFailures);
        Interlocked.Increment(ref _consecutiveFailures);
    }

    /// <summary>Reset health state (called by health monitor on recovery).</summary>
    public void ResetHealth()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
    }

    public override string ToString()
    {
        return $"{Name} ({Url}) - Healthy={IsHealthy}, EMA={EmaResponseTimeMs:F1}ms, " +
               $"Requests={TotalRequests}, Failures={TotalFailures}";
    }
}
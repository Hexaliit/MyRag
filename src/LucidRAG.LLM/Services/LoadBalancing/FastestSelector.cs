namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
/// Selects the endpoint with the lowest EMA response time.
/// Falls back to round-robin when no latency data is available.
/// Allocation-free: single-pass for loop with inline round-robin fallback.
/// </summary>
public sealed class FastestSelector : IEndpointSelector
{
    private readonly RoundRobinSelector _fallback = new();

    public EndpointState? Select(IReadOnlyList<EndpointState> endpoints)
    {
        if (endpoints.Count == 0) return null;

        // Single pass: find the fastest healthy endpoint with EMA data,
        // and count healthy endpoints with no data simultaneously.
        EndpointState? fastest = null;
        var lowestEma = double.MaxValue;
        var healthyNoDataCount = 0;

        for (var i = 0; i < endpoints.Count; i++)
        {
            var ep = endpoints[i];
            if (!ep.IsHealthy) continue;

            var ema = ep.EmaResponseTimeMs;
            if (ema >= double.MaxValue)
            {
                healthyNoDataCount++;
            }
            else if (ema < lowestEma)
            {
                lowestEma = ema;
                fastest = ep;
            }
        }

        if (fastest != null) return fastest;

        // All healthy endpoints have no latency data — delegate to round-robin
        if (healthyNoDataCount > 0) return _fallback.Select(endpoints);

        return null; // no healthy endpoints
    }
}

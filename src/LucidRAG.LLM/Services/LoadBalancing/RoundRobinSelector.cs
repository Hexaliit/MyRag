namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
///     Round-robin endpoint selection across healthy endpoints.
///     Allocation-free: uses a for loop instead of LINQ.
/// </summary>
public sealed class RoundRobinSelector : IEndpointSelector
{
    private int _counter = -1;

    public EndpointState? Select(IReadOnlyList<EndpointState> endpoints)
    {
        if (endpoints.Count == 0) return null;

        // Count healthy endpoints in a single pass (no allocation)
        var healthyCount = 0;
        for (var i = 0; i < endpoints.Count; i++)
            if (endpoints[i].IsHealthy)
                healthyCount++;

        if (healthyCount == 0) return null;

        // Pick the Nth healthy endpoint via counter modulo
        var target = (int)((uint)Interlocked.Increment(ref _counter) % healthyCount);
        var seen = 0;
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (!endpoints[i].IsHealthy) continue;
            if (seen == target) return endpoints[i];
            seen++;
        }

        return null; // unreachable if healthyCount > 0
    }
}
using System.Diagnostics.Metrics;

namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
/// Shared OpenTelemetry metrics for load balancing.
/// Single Meter instance avoids duplicate instrument registration.
/// </summary>
internal static class LoadBalancingMetrics
{
    internal static readonly Meter Meter = new("LucidRAG.LLM.LoadBalancing", "1.0.0");

    // LoadBalancedLlmService counters
    internal static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>("lucidrag.lb.requests");
    internal static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>("lucidrag.lb.errors");
    internal static readonly Counter<long> FailoverCounter = Meter.CreateCounter<long>("lucidrag.lb.failovers");
    internal static readonly Histogram<double> LatencyHistogram = Meter.CreateHistogram<double>("lucidrag.lb.latency_ms");

    // EndpointHealthMonitor counters
    internal static readonly Counter<long> ProbeCounter = Meter.CreateCounter<long>("lucidrag.lb.health_probes");
    internal static readonly Counter<long> RecoveryCounter = Meter.CreateCounter<long>("lucidrag.lb.recoveries");
}

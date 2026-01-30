using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
/// Background health monitor that probes unhealthy endpoints and restores them on recovery.
/// </summary>
public sealed class EndpointHealthMonitor : IDisposable
{
    private readonly IReadOnlyList<EndpointState> _endpoints;
    private readonly IReadOnlyDictionary<string, ILlmService> _services;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _probeLoop;

    public EndpointHealthMonitor(
        IReadOnlyList<EndpointState> endpoints,
        IReadOnlyDictionary<string, ILlmService> services,
        int intervalSeconds,
        ILogger logger)
    {
        _endpoints = endpoints;
        _services = services;
        _interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, 5));
        _logger = logger;
        _probeLoop = Task.Run(ProbeLoopAsync);
    }

    private async Task ProbeLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var endpoint in _endpoints)
            {
                if (_cts.IsCancellationRequested) break;
                if (endpoint.IsHealthy) continue;
                if (!_services.TryGetValue(endpoint.Url, out var service)) continue;

                LoadBalancingMetrics.ProbeCounter.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint.Name));

                try
                {
                    var available = await service.IsAvailableAsync(_cts.Token);
                    if (available)
                    {
                        endpoint.ResetHealth();
                        LoadBalancingMetrics.RecoveryCounter.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint.Name));
                        _logger.LogInformation("Endpoint {Endpoint} recovered", endpoint.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Health probe failed for {Endpoint}", endpoint.Name);
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

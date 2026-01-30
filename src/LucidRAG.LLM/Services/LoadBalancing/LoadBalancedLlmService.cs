using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services.LoadBalancing;

/// <summary>
/// ILlmService wrapper that distributes requests across multiple endpoint-specific services
/// with automatic failover and health tracking.
/// </summary>
public sealed class LoadBalancedLlmService : ILlmService, IDisposable
{
    private readonly IReadOnlyList<EndpointState> _endpoints;
    private readonly IReadOnlyDictionary<string, ILlmService> _services;
    private readonly IEndpointSelector _selector;
    private readonly EndpointHealthMonitor? _healthMonitor;
    private readonly ILogger _logger;
    private readonly string _backendName;
    private Lazy<Task<int>>? _cachedContextWindow;

    public LoadBalancedLlmService(
        string backendName,
        IReadOnlyList<EndpointState> endpoints,
        IReadOnlyDictionary<string, ILlmService> services,
        IEndpointSelector selector,
        ILogger logger,
        int healthCheckIntervalSeconds = 30)
    {
        _backendName = backendName;
        _endpoints = endpoints;
        _services = services;
        _selector = selector;
        _logger = logger;

        if (healthCheckIntervalSeconds > 0)
        {
            _healthMonitor = new EndpointHealthMonitor(endpoints, services, healthCheckIntervalSeconds, logger);
        }
    }

    public string ProviderName => $"LoadBalanced({_backendName}, {_endpoints.Count} endpoints)";

    public async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        return await ExecuteWithFailover(
            async (service, token) => await service.GenerateAsync(prompt, options, token),
            ct);
    }

    public async Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default) where T : class
    {
        return await ExecuteWithFailover(
            async (service, token) => await service.GenerateJsonAsync<T>(prompt, options, token),
            ct);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return _endpoints.Any(e => e.IsHealthy);
    }

    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        _cachedContextWindow ??= new Lazy<Task<int>>(() => FetchContextWindowAsync(ct));
        return _cachedContextWindow.Value;
    }

    private async Task<int> FetchContextWindowAsync(CancellationToken ct)
    {
        var endpoint = _selector.Select(_endpoints);
        if (endpoint == null)
            throw new InvalidOperationException("No healthy endpoints available");

        return await _services[endpoint.Url].GetContextWindowAsync(ct);
    }

    private async Task<T> ExecuteWithFailover<T>(
        Func<ILlmService, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var tried = new HashSet<string>();
        Exception? lastException = null;

        while (tried.Count < _endpoints.Count)
        {
            // Ask the selector for its preferred endpoint
            var endpoint = _selector.Select(_endpoints);

            // If selector returned null or already-tried, scan for any untried healthy endpoint
            if (endpoint == null || tried.Contains(endpoint.Url))
            {
                endpoint = _endpoints.FirstOrDefault(e => e.IsHealthy && !tried.Contains(e.Url));
                if (endpoint == null) break;
            }

            tried.Add(endpoint.Url);

            if (!_services.TryGetValue(endpoint.Url, out var service))
                continue;

            LoadBalancingMetrics.RequestCounter.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint.Name));
            var sw = Stopwatch.StartNew();

            try
            {
                var result = await action(service, ct);
                sw.Stop();

                endpoint.RecordSuccess(sw.Elapsed.TotalMilliseconds);
                LoadBalancingMetrics.LatencyHistogram.Record(sw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("endpoint", endpoint.Name));

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                endpoint.RecordFailure();
                lastException = ex;

                LoadBalancingMetrics.ErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint.Name));
                LoadBalancingMetrics.FailoverCounter.Add(1,
                    new KeyValuePair<string, object?>("from", endpoint.Name));

                _logger.LogWarning(ex,
                    "Endpoint {Endpoint} failed, attempting failover ({Tried}/{Total})",
                    endpoint.Name, tried.Count, _endpoints.Count);
            }
        }

        throw new InvalidOperationException(
            $"All {_endpoints.Count} endpoints exhausted for {_backendName}",
            lastException);
    }

    public void Dispose()
    {
        _healthMonitor?.Dispose();
    }
}

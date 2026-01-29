using System.Collections.Concurrent;
using System.Net;
using DoomSummarizer.Models;
using Polly;
using Polly.Retry;
namespace DoomSummarizer.Services;

/// <summary>
/// Centralized resilience for API calls: per-service rate limiting, retry with
/// backoff, and circuit breaker. All search services route through this.
/// Keyed by service name — each service gets its own pipeline.
/// Config is read from ApiKeyEntry fields (rateLimitMs, maxRetries, etc.).
/// </summary>
public static class ApiRateLimiter
{
    // Defaults when no config entry exists
    private static readonly Dictionary<string, int> DefaultDelayMs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave_search"] = 1100,
        ["serper"] = 200,
        ["tavily"] = 200,
        ["newsapi"] = 200,
        ["newsdata"] = 200,
        ["jina"] = 500,
        ["anthropic"] = 100,
        ["openai"] = 100,
        ["duckduckgo"] = 2000,
        ["currents"] = 200,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Semaphores = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastCall = new();
    private static readonly ConcurrentDictionary<string, ResiliencePipeline<HttpResponseMessage>> Pipelines = new();
    private static readonly ConcurrentDictionary<string, int> ConsecutiveFailures = new();
    private static readonly ConcurrentDictionary<string, ApiKeyEntry> ServiceConfigs = new();
    private static CircuitBreakerService? _circuitBreaker;

    /// <summary>
    /// Set the persistent circuit breaker. Call once during startup after initializing CircuitBreakerService.
    /// </summary>
    public static void SetCircuitBreaker(CircuitBreakerService circuitBreaker)
        => _circuitBreaker = circuitBreaker;

    /// <summary>
    /// Register service configuration. Call during setup to load per-service resilience settings.
    /// </summary>
    public static void Configure(ApiKeyService keys)
    {
        foreach (var svc in keys.GetConfiguredServices())
        {
            if (!string.IsNullOrEmpty(svc.Name))
                ServiceConfigs[svc.Name] = svc;
        }
    }

    /// <summary>
    /// Acquire a rate-limited slot for the given service.
    /// Returns a disposable that releases the slot when done.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string service, CancellationToken ct = default)
    {
        var sem = Semaphores.GetOrAdd(service, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);

        if (LastCall.TryGetValue(service, out var last))
        {
            var minDelay = GetDelay(service);
            var elapsed = (DateTimeOffset.UtcNow - last).TotalMilliseconds;
            if (elapsed < minDelay)
                await Task.Delay((int)(minDelay - elapsed), ct);
        }

        return new RateSlot(service, sem);
    }

    /// <summary>
    /// Execute an HTTP request through the full resilience pipeline:
    /// rate limit → retry (429/5xx) → circuit breaker.
    /// </summary>
    public static async Task<HttpResponseMessage> ExecuteAsync(
        string service, Func<CancellationToken, Task<HttpResponseMessage>> action,
        CancellationToken ct = default)
    {
        if (IsCircuitOpen(service))
        {
            System.Diagnostics.Debug.WriteLine($"{service}: circuit open -- skipping");
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        using var slot = await AcquireAsync(service, ct);

        var pipeline = Pipelines.GetOrAdd(service, BuildPipeline);
        var result = await pipeline.ExecuteAsync(async token => await action(token), ct);

        // Track circuit state (in-memory fast path)
        if (result.IsSuccessStatusCode)
        {
            ConsecutiveFailures[service] = 0;
            if (_circuitBreaker != null)
                _ = _circuitBreaker.ReportSuccessAsync(service);
        }
        else if ((int)result.StatusCode >= 500 || result.StatusCode == HttpStatusCode.TooManyRequests)
        {
            ConsecutiveFailures.AddOrUpdate(service, 1, (_, n) => n + 1);
            if (_circuitBreaker != null)
            {
                var failureType = CircuitBreakerService.ClassifyHttpStatus((int)result.StatusCode);
                _ = _circuitBreaker.ReportFailureAsync(service, failureType,
                    $"HTTP {(int)result.StatusCode}");
            }
        }
        else if (result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (_circuitBreaker != null)
            {
                _ = _circuitBreaker.ReportFailureAsync(service,
                    CircuitFailureType.AuthError,
                    $"HTTP {(int)result.StatusCode}");
            }
        }

        return result;
    }

    /// <summary>Circuit breaker: checks persistent circuit first, then in-memory consecutive failures.</summary>
    public static bool IsCircuitOpen(string service)
    {
        // Check persistent circuit first
        if (_circuitBreaker?.IsCircuitOpen(service) == true)
            return true;

        // Fall back to in-memory consecutive failures (fast path for current session)
        var threshold = GetCircuitThreshold(service);
        if (threshold <= 0) return false;

        if (!ConsecutiveFailures.TryGetValue(service, out var n) || n < threshold)
            return false;

        if (LastCall.TryGetValue(service, out var last) &&
            (DateTimeOffset.UtcNow - last).TotalSeconds > GetCircuitResetSeconds(service))
        {
            ConsecutiveFailures[service] = 0;
            return false;
        }

        return true;
    }

    public static void ResetCircuit(string service) => ConsecutiveFailures[service] = 0;

    private static int GetDelay(string service) =>
        ServiceConfigs.TryGetValue(service, out var cfg) && cfg.RateLimitMs > 0
            ? cfg.RateLimitMs
            : DefaultDelayMs.GetValueOrDefault(service, 200);

    private static int GetMaxRetries(string service) =>
        ServiceConfigs.TryGetValue(service, out var cfg) ? cfg.MaxRetries : 2;

    private static int GetCircuitThreshold(string service) =>
        ServiceConfigs.TryGetValue(service, out var cfg) ? cfg.CircuitBreakerThreshold : 3;

    private static int GetCircuitResetSeconds(string service) =>
        ServiceConfigs.TryGetValue(service, out var cfg) && cfg.CircuitBreakerResetSeconds > 0
            ? cfg.CircuitBreakerResetSeconds
            : 60;

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(string service)
    {
        var maxRetries = GetMaxRetries(service);
        if (maxRetries <= 0)
            return ResiliencePipeline<HttpResponseMessage>.Empty;

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(GetDelay(service)),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
                    .HandleResult(r => (int)r.StatusCode >= 500),
                OnRetry = args =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"{service}: {(int)(args.Outcome.Result?.StatusCode ?? 0)} -- retry {args.AttemptNumber + 1}");
                    return default;
                }
            })
            .Build();
    }

    private sealed class RateSlot(string service, SemaphoreSlim sem) : IDisposable
    {
        public void Dispose()
        {
            LastCall[service] = DateTimeOffset.UtcNow;
            sem.Release();
        }
    }
}

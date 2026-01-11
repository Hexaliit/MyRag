using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
/// Ephemeral atoms for capability-based processing.
/// Provides rate limiting, time estimation, and backpressure management.
/// Designed to keep ingestion pipelines full while maintaining UI responsiveness.
/// </summary>
public static class CapabilityAtoms
{
    /// <summary>
    /// Creates a rate limiter that allows N operations per time window.
    /// </summary>
    public static IRateLimiter CreateRateLimiter(int maxOps, TimeSpan window) =>
        new SlidingWindowRateLimiter(maxOps, window);

    /// <summary>
    /// Creates a time estimator that tracks operation durations.
    /// </summary>
    public static ITimeEstimator CreateTimeEstimator() =>
        new RollingAverageTimeEstimator();

    /// <summary>
    /// Creates a backpressure controller for balancing throughput and responsiveness.
    /// </summary>
    public static IBackpressureController CreateBackpressureController(
        int minConcurrency = 1,
        int? maxConcurrency = null,
        TimeSpan? targetLatency = null) =>
        new AdaptiveBackpressureController(minConcurrency, maxConcurrency ?? Environment.ProcessorCount, targetLatency ?? TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Creates a pipeline balancer that keeps queues filled while respecting backpressure.
    /// </summary>
    public static IPipelineBalancer CreatePipelineBalancer(
        int targetQueueDepth = 2,
        int maxQueueDepth = 10) =>
        new AdaptivePipelineBalancer(targetQueueDepth, maxQueueDepth);
}

/// <summary>
/// Rate limiter interface.
/// </summary>
public interface IRateLimiter
{
    Task<bool> TryAcquireAsync(CancellationToken ct = default);
    Task WaitAsync(CancellationToken ct = default);
    double CurrentRate { get; }
}

/// <summary>
/// Sliding window rate limiter implementation.
/// </summary>
public class SlidingWindowRateLimiter : IRateLimiter
{
    private readonly int _maxOps;
    private readonly TimeSpan _window;
    private readonly ConcurrentQueue<DateTimeOffset> _timestamps = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SlidingWindowRateLimiter(int maxOps, TimeSpan window)
    {
        _maxOps = maxOps;
        _window = window;
    }

    public double CurrentRate
    {
        get
        {
            PruneOldTimestamps();
            return _timestamps.Count / _window.TotalSeconds;
        }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            PruneOldTimestamps();

            if (_timestamps.Count >= _maxOps)
                return false;

            _timestamps.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (!await TryAcquireAsync(ct))
        {
            // Calculate wait time based on oldest timestamp
            if (_timestamps.TryPeek(out var oldest))
            {
                var waitTime = oldest + _window - DateTimeOffset.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    await Task.Delay(waitTime, ct);
                }
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }

    private void PruneOldTimestamps()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        while (_timestamps.TryPeek(out var ts) && ts < cutoff)
        {
            _timestamps.TryDequeue(out _);
        }
    }
}

/// <summary>
/// Time estimator interface for tracking operation durations.
/// </summary>
public interface ITimeEstimator
{
    void RecordOperation(string operationType, TimeSpan duration);
    TimeSpan EstimateRemaining(string operationType, int remainingCount);
    TimeSpan GetAverageDuration(string operationType);
    TimeEstimate GetEstimate(string operationType, int remainingCount);
}

/// <summary>
/// Time estimate with confidence interval.
/// </summary>
public record TimeEstimate
{
    public TimeSpan Estimated { get; init; }
    public TimeSpan Optimistic { get; init; }
    public TimeSpan Pessimistic { get; init; }
    public int SampleCount { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// Rolling average time estimator with statistical tracking.
/// </summary>
public class RollingAverageTimeEstimator : ITimeEstimator
{
    private readonly ConcurrentDictionary<string, OperationStats> _stats = new();
    private const int MaxSamples = 100;

    public void RecordOperation(string operationType, TimeSpan duration)
    {
        var stats = _stats.GetOrAdd(operationType, _ => new OperationStats());
        stats.Record(duration);
    }

    public TimeSpan EstimateRemaining(string operationType, int remainingCount)
    {
        var avg = GetAverageDuration(operationType);
        return TimeSpan.FromMilliseconds(avg.TotalMilliseconds * remainingCount);
    }

    public TimeSpan GetAverageDuration(string operationType)
    {
        if (!_stats.TryGetValue(operationType, out var stats))
            return TimeSpan.Zero;

        return stats.GetAverage();
    }

    public TimeEstimate GetEstimate(string operationType, int remainingCount)
    {
        if (!_stats.TryGetValue(operationType, out var stats) || stats.Count == 0)
        {
            return new TimeEstimate
            {
                Estimated = TimeSpan.Zero,
                Optimistic = TimeSpan.Zero,
                Pessimistic = TimeSpan.Zero,
                SampleCount = 0,
                Confidence = 0
            };
        }

        var (avg, min, max, stdDev) = stats.GetStatistics();

        return new TimeEstimate
        {
            Estimated = TimeSpan.FromMilliseconds(avg * remainingCount),
            Optimistic = TimeSpan.FromMilliseconds(min * remainingCount),
            Pessimistic = TimeSpan.FromMilliseconds(max * remainingCount),
            SampleCount = stats.Count,
            // Confidence increases with more samples, up to 0.95
            Confidence = Math.Min(0.95, 0.5 + (stats.Count / 200.0))
        };
    }

    private class OperationStats
    {
        private readonly ConcurrentQueue<double> _samples = new();
        private double _sum;

        public int Count => _samples.Count;

        public void Record(TimeSpan duration)
        {
            var ms = duration.TotalMilliseconds;
            _samples.Enqueue(ms);
            Interlocked.Exchange(ref _sum, _sum + ms);

            // Prune old samples
            while (_samples.Count > MaxSamples && _samples.TryDequeue(out var old))
            {
                Interlocked.Exchange(ref _sum, _sum - old);
            }
        }

        public TimeSpan GetAverage()
        {
            var count = _samples.Count;
            if (count == 0) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(_sum / count);
        }

        public (double avg, double min, double max, double stdDev) GetStatistics()
        {
            var samples = _samples.ToArray();
            if (samples.Length == 0) return (0, 0, 0, 0);

            var avg = samples.Average();
            var min = samples.Min();
            var max = samples.Max();
            var variance = samples.Select(x => Math.Pow(x - avg, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            return (avg, min, max, stdDev);
        }
    }
}

/// <summary>
/// Backpressure controller interface.
/// </summary>
public interface IBackpressureController
{
    int CurrentConcurrency { get; }
    Task<IDisposable> AcquireSlotAsync(CancellationToken ct = default);
    void RecordLatency(TimeSpan latency);
    BackpressureStatus GetStatus();
}

/// <summary>
/// Backpressure status for monitoring.
/// </summary>
public record BackpressureStatus
{
    public int CurrentConcurrency { get; init; }
    public int MaxConcurrency { get; init; }
    public double AverageLatencyMs { get; init; }
    public double TargetLatencyMs { get; init; }
    public bool IsThrottling { get; init; }
}

/// <summary>
/// Adaptive backpressure controller that adjusts concurrency based on latency.
/// </summary>
public class AdaptiveBackpressureController : IBackpressureController
{
    private readonly int _minConcurrency;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _targetLatency;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentQueue<double> _latencies = new();
    private int _currentConcurrency;
    private readonly object _adjustLock = new();

    public AdaptiveBackpressureController(int minConcurrency, int maxConcurrency, TimeSpan targetLatency)
    {
        _minConcurrency = minConcurrency;
        _maxConcurrency = maxConcurrency;
        _targetLatency = targetLatency;
        _currentConcurrency = minConcurrency;
        _semaphore = new SemaphoreSlim(minConcurrency, maxConcurrency);
    }

    public int CurrentConcurrency => _currentConcurrency;

    public async Task<IDisposable> AcquireSlotAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new SlotReleaser(this);
    }

    public void RecordLatency(TimeSpan latency)
    {
        _latencies.Enqueue(latency.TotalMilliseconds);

        // Keep only recent samples
        while (_latencies.Count > 50)
        {
            _latencies.TryDequeue(out _);
        }

        // Adjust concurrency based on latency
        AdjustConcurrency();
    }

    public BackpressureStatus GetStatus()
    {
        var avgLatency = _latencies.Count > 0 ? _latencies.Average() : 0;
        return new BackpressureStatus
        {
            CurrentConcurrency = _currentConcurrency,
            MaxConcurrency = _maxConcurrency,
            AverageLatencyMs = avgLatency,
            TargetLatencyMs = _targetLatency.TotalMilliseconds,
            IsThrottling = avgLatency > _targetLatency.TotalMilliseconds * 1.5
        };
    }

    private void AdjustConcurrency()
    {
        if (_latencies.Count < 10) return;

        lock (_adjustLock)
        {
            var avgLatency = _latencies.Average();
            var targetMs = _targetLatency.TotalMilliseconds;

            if (avgLatency < targetMs * 0.5 && _currentConcurrency < _maxConcurrency)
            {
                // Latency is well below target - increase concurrency
                _currentConcurrency = Math.Min(_currentConcurrency + 1, _maxConcurrency);
                _semaphore.Release();
            }
            else if (avgLatency > targetMs * 1.5 && _currentConcurrency > _minConcurrency)
            {
                // Latency is too high - decrease concurrency
                // We decrease by waiting for a slot to be released and not re-releasing it
                _currentConcurrency = Math.Max(_currentConcurrency - 1, _minConcurrency);
            }
        }
    }

    private void ReleaseSlot()
    {
        // Only release if we haven't reduced concurrency
        lock (_adjustLock)
        {
            if (_semaphore.CurrentCount < _currentConcurrency)
            {
                _semaphore.Release();
            }
        }
    }

    private class SlotReleaser(AdaptiveBackpressureController controller) : IDisposable
    {
        public void Dispose() => controller.ReleaseSlot();
    }
}

/// <summary>
/// Pipeline balancer interface for keeping work queues optimally filled.
/// </summary>
public interface IPipelineBalancer
{
    int SuggestedPrefetch { get; }
    void RecordQueueDepth(int depth);
    void RecordStarvation();
    void RecordOverflow();
    PipelineBalance GetBalance();
}

/// <summary>
/// Pipeline balance status.
/// </summary>
public record PipelineBalance
{
    public int SuggestedPrefetch { get; init; }
    public int StarvedCount { get; init; }
    public int OverflowCount { get; init; }
    public double Efficiency { get; init; }
}

/// <summary>
/// Adaptive pipeline balancer that adjusts prefetch depth based on consumption patterns.
/// </summary>
public class AdaptivePipelineBalancer : IPipelineBalancer
{
    private readonly int _targetDepth;
    private readonly int _maxDepth;
    private int _suggestedPrefetch;
    private int _starvedCount;
    private int _overflowCount;
    private readonly ConcurrentQueue<int> _depthHistory = new();

    public AdaptivePipelineBalancer(int targetDepth, int maxDepth)
    {
        _targetDepth = targetDepth;
        _maxDepth = maxDepth;
        _suggestedPrefetch = targetDepth;
    }

    public int SuggestedPrefetch => _suggestedPrefetch;

    public void RecordQueueDepth(int depth)
    {
        _depthHistory.Enqueue(depth);
        while (_depthHistory.Count > 100)
        {
            _depthHistory.TryDequeue(out _);
        }

        // Adjust prefetch based on average depth
        if (_depthHistory.Count >= 10)
        {
            var avgDepth = _depthHistory.Average();

            if (avgDepth < _targetDepth * 0.5)
            {
                // Queue is often empty - increase prefetch
                _suggestedPrefetch = Math.Min(_suggestedPrefetch + 1, _maxDepth);
            }
            else if (avgDepth > _targetDepth * 1.5)
            {
                // Queue is often full - decrease prefetch
                _suggestedPrefetch = Math.Max(_suggestedPrefetch - 1, 1);
            }
        }
    }

    public void RecordStarvation()
    {
        Interlocked.Increment(ref _starvedCount);
        // Immediate response to starvation
        _suggestedPrefetch = Math.Min(_suggestedPrefetch + 1, _maxDepth);
    }

    public void RecordOverflow()
    {
        Interlocked.Increment(ref _overflowCount);
        // Reduce prefetch on overflow
        _suggestedPrefetch = Math.Max(_suggestedPrefetch - 1, 1);
    }

    public PipelineBalance GetBalance()
    {
        var total = _starvedCount + _overflowCount;
        var efficiency = total > 0
            ? 1.0 - ((double)_starvedCount + _overflowCount) / Math.Max(total * 2, 100)
            : 1.0;

        return new PipelineBalance
        {
            SuggestedPrefetch = _suggestedPrefetch,
            StarvedCount = _starvedCount,
            OverflowCount = _overflowCount,
            Efficiency = efficiency
        };
    }
}

/// <summary>
/// Scoped timing helper for recording operation durations.
/// </summary>
public readonly struct TimedOperation : IDisposable
{
    private readonly ITimeEstimator _estimator;
    private readonly string _operationType;
    private readonly Stopwatch _stopwatch;

    public TimedOperation(ITimeEstimator estimator, string operationType)
    {
        _estimator = estimator;
        _operationType = operationType;
        _stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        _estimator.RecordOperation(_operationType, _stopwatch.Elapsed);
    }
}

/// <summary>
/// Extension methods for capability atoms.
/// </summary>
public static class CapabilityAtomExtensions
{
    /// <summary>
    /// Time an operation and record its duration.
    /// </summary>
    public static TimedOperation Time(this ITimeEstimator estimator, string operationType) =>
        new(estimator, operationType);
}

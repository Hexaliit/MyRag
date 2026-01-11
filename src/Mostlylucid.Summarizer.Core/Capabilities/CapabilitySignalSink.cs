using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
/// Signal types for capability changes.
/// </summary>
public enum CapabilitySignalType
{
    // Model lifecycle
    ModelDownloadStarted,
    ModelDownloadProgress,
    ModelDownloadFailed,
    ModelAvailable,
    ModelUnloaded,

    // GPU/Hardware
    GpuAvailable,
    GpuUnavailable,
    ProviderChanged,

    // Component lifecycle
    ComponentActivated,
    ComponentDeactivated,
    ComponentReady,
    ComponentBusy,
    ComponentError,

    // System
    CapabilitiesRefreshed,
    ConfigurationChanged,

    // Mesh (future)
    NodeJoined,
    NodeLeft,
    WorkRouted
}

/// <summary>
/// A capability signal that can be emitted and observed.
/// Designed to be serializable for mesh distribution.
/// </summary>
public record CapabilitySignal
{
    public required CapabilitySignalType SignalType { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    // Optional context based on signal type
    public string? ModelId { get; init; }
    public string? ComponentId { get; init; }
    public string? ProviderId { get; init; }
    public string? NodeId { get; init; }
    public string? LocalPath { get; init; }
    public string? Error { get; init; }
    public double? Progress { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Interface for capability signal sink - shared bus for coordinator communication.
/// </summary>
public interface ICapabilitySignalSink
{
    /// <summary>
    /// Emit a signal to all subscribers.
    /// </summary>
    Task EmitAsync(CapabilitySignal signal, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to signals matching a filter.
    /// </summary>
    IDisposable Subscribe(
        Action<CapabilitySignal> handler,
        Func<CapabilitySignal, bool>? filter = null);

    /// <summary>
    /// Subscribe to signals as an async enumerable.
    /// </summary>
    IAsyncEnumerable<CapabilitySignal> ObserveAsync(
        Func<CapabilitySignal, bool>? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Wait for a specific signal condition.
    /// </summary>
    Task<CapabilitySignal> WaitForAsync(
        Func<CapabilitySignal, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get recent signal history (for late subscribers / debugging).
    /// </summary>
    IReadOnlyList<CapabilitySignal> GetRecentSignals(int count = 100);
}

/// <summary>
/// In-process capability signal sink implementation.
/// For mesh distribution, this would be replaced with a distributed pub/sub.
/// </summary>
public class CapabilitySignalSink : ICapabilitySignalSink, IDisposable
{
    private readonly ILogger<CapabilitySignalSink> _logger;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly ConcurrentQueue<CapabilitySignal> _signalHistory = new();
    private readonly int _maxHistorySize;
    private readonly SemaphoreSlim _emitLock = new(1, 1);

    public CapabilitySignalSink(ILogger<CapabilitySignalSink> logger, int maxHistorySize = 1000)
    {
        _logger = logger;
        _maxHistorySize = maxHistorySize;
    }

    public async Task EmitAsync(CapabilitySignal signal, CancellationToken ct = default)
    {
        // Add to history
        _signalHistory.Enqueue(signal);
        while (_signalHistory.Count > _maxHistorySize)
        {
            _signalHistory.TryDequeue(out _);
        }

        _logger.LogDebug("Emitting signal {SignalType} for {ModelId}/{ComponentId}",
            signal.SignalType, signal.ModelId, signal.ComponentId);

        // Notify all subscriptions
        foreach (var (_, subscription) in _subscriptions)
        {
            try
            {
                if (subscription.Filter == null || subscription.Filter(signal))
                {
                    subscription.Handler?.Invoke(signal);
                    subscription.Channel?.Writer.TryWrite(signal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in signal handler");
            }
        }

        await Task.CompletedTask;
    }

    public IDisposable Subscribe(Action<CapabilitySignal> handler, Func<CapabilitySignal, bool>? filter = null)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = id,
            Handler = handler,
            Filter = filter
        };

        _subscriptions[id] = subscription;

        return new SubscriptionDisposer(() => _subscriptions.TryRemove(id, out _));
    }

    public async IAsyncEnumerable<CapabilitySignal> ObserveAsync(
        Func<CapabilitySignal, bool>? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<CapabilitySignal>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        var subscription = new Subscription
        {
            Id = id,
            Channel = channel,
            Filter = filter
        };

        _subscriptions[id] = subscription;

        try
        {
            await foreach (var signal in channel.Reader.ReadAllAsync(ct))
            {
                yield return signal;
            }
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    public async Task<CapabilitySignal> WaitForAsync(
        Func<CapabilitySignal, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CapabilitySignal>();

        using var subscription = Subscribe(signal =>
        {
            if (predicate(signal))
            {
                tcs.TrySetResult(signal);
            }
        });

        var timeoutTask = timeout.HasValue
            ? Task.Delay(timeout.Value, ct)
            : Task.Delay(Timeout.Infinite, ct);

        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException("Timed out waiting for signal");
        }

        return await tcs.Task;
    }

    public IReadOnlyList<CapabilitySignal> GetRecentSignals(int count = 100)
    {
        return _signalHistory.TakeLast(count).ToList();
    }

    public void Dispose()
    {
        foreach (var (_, subscription) in _subscriptions)
        {
            subscription.Channel?.Writer.TryComplete();
        }
        _subscriptions.Clear();
        _emitLock.Dispose();
    }

    private class Subscription
    {
        public Guid Id { get; init; }
        public Action<CapabilitySignal>? Handler { get; init; }
        public Channel<CapabilitySignal>? Channel { get; init; }
        public Func<CapabilitySignal, bool>? Filter { get; init; }
    }

    private class SubscriptionDisposer(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

/// <summary>
/// Extension methods for signal filtering.
/// </summary>
public static class CapabilitySignalExtensions
{
    /// <summary>
    /// Filter to model-related signals.
    /// </summary>
    public static Func<CapabilitySignal, bool> ModelSignals(string? modelId = null) =>
        s => s.SignalType is CapabilitySignalType.ModelDownloadStarted
            or CapabilitySignalType.ModelDownloadProgress
            or CapabilitySignalType.ModelDownloadFailed
            or CapabilitySignalType.ModelAvailable
            or CapabilitySignalType.ModelUnloaded
            && (modelId == null || s.ModelId == modelId);

    /// <summary>
    /// Filter to component-related signals.
    /// </summary>
    public static Func<CapabilitySignal, bool> ComponentSignals(string? componentId = null) =>
        s => s.SignalType is CapabilitySignalType.ComponentActivated
            or CapabilitySignalType.ComponentDeactivated
            or CapabilitySignalType.ComponentReady
            or CapabilitySignalType.ComponentBusy
            or CapabilitySignalType.ComponentError
            && (componentId == null || s.ComponentId == componentId);

    /// <summary>
    /// Wait for a model to become available.
    /// </summary>
    public static Task<CapabilitySignal> WaitForModelAsync(
        this ICapabilitySignalSink sink,
        string modelId,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        sink.WaitForAsync(
            s => s.SignalType == CapabilitySignalType.ModelAvailable && s.ModelId == modelId,
            timeout,
            ct);

    /// <summary>
    /// Wait for a component to be ready.
    /// </summary>
    public static Task<CapabilitySignal> WaitForComponentReadyAsync(
        this ICapabilitySignalSink sink,
        string componentId,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        sink.WaitForAsync(
            s => s.SignalType == CapabilitySignalType.ComponentReady && s.ComponentId == componentId,
            timeout,
            ct);
}

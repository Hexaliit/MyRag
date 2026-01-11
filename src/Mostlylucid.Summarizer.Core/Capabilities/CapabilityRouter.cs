using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
/// Routes work based on available capabilities.
/// Responds reactively to capability changes via signals.
/// Supports fallback chains and work queuing until capabilities become available.
/// </summary>
public class CapabilityRouter : IDisposable
{
    private readonly ILogger<CapabilityRouter> _logger;
    private readonly CapabilityRegistry _registry;
    private readonly BackgroundModelDownloader _downloader;
    private readonly ICapabilitySignalSink _signalSink;

    // Component availability tracking
    private readonly ConcurrentDictionary<string, ComponentState> _componentStates = new();

    // Pending work waiting for capabilities
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingWork>> _pendingWork = new();

    // Signal subscription
    private readonly IDisposable _signalSubscription;

    public CapabilityRouter(
        ILogger<CapabilityRouter> logger,
        CapabilityRegistry registry,
        BackgroundModelDownloader downloader,
        ICapabilitySignalSink signalSink)
    {
        _logger = logger;
        _registry = registry;
        _downloader = downloader;
        _signalSink = signalSink;

        // Subscribe to capability signals
        _signalSubscription = signalSink.Subscribe(OnSignalReceived);
    }

    /// <summary>
    /// Register a component with its required capabilities.
    /// </summary>
    public void RegisterComponent(ComponentRegistration registration)
    {
        var state = new ComponentState
        {
            Registration = registration,
            Status = ComponentStatus.Initializing,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        _componentStates[registration.ComponentId] = state;
        _pendingWork[registration.ComponentId] = new ConcurrentQueue<PendingWork>();

        _logger.LogInformation("Registered component {ComponentId} requiring models: {Models}",
            registration.ComponentId, string.Join(", ", registration.RequiredModels));
    }

    /// <summary>
    /// Check if a component is ready to accept work.
    /// </summary>
    public async Task<bool> IsComponentReadyAsync(string componentId, CancellationToken ct = default)
    {
        if (!_componentStates.TryGetValue(componentId, out var state))
            return false;

        if (state.Status == ComponentStatus.Ready)
            return true;

        // Check if all required models are available
        var availability = _downloader.CheckModelAvailability();
        var allModelsAvailable = state.Registration.RequiredModels
            .All(m => availability.GetValueOrDefault(m, false));

        if (!allModelsAvailable)
            return false;

        // Check if required providers are available
        var capabilities = await _registry.GetCapabilitiesAsync(ct);
        var hasRequiredProvider = state.Registration.RequiredProviders.Count == 0 ||
            state.Registration.RequiredProviders.Any(p => capabilities.HasProvider(p));

        if (!hasRequiredProvider)
            return false;

        // Update state to ready
        state.Status = ComponentStatus.Ready;
        state.BecameReadyAt = DateTimeOffset.UtcNow;

        await _signalSink.EmitAsync(new CapabilitySignal
        {
            SignalType = CapabilitySignalType.ComponentReady,
            ComponentId = componentId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        // Process any pending work
        await ProcessPendingWorkAsync(componentId, ct);

        return true;
    }

    /// <summary>
    /// Route work to the best available component from a fallback chain.
    /// </summary>
    public async Task<RouteResult> RouteAsync(
        string[] fallbackChain,
        CancellationToken ct = default)
    {
        foreach (var componentId in fallbackChain)
        {
            if (await IsComponentReadyAsync(componentId, ct))
            {
                return new RouteResult
                {
                    Success = true,
                    SelectedComponent = componentId,
                    IsFallback = componentId != fallbackChain[0]
                };
            }
        }

        // No component ready - return best option with pending status
        var primary = fallbackChain.FirstOrDefault();
        return new RouteResult
        {
            Success = false,
            SelectedComponent = primary,
            PendingReason = primary != null
                ? GetPendingReason(primary)
                : "No components in fallback chain"
        };
    }

    /// <summary>
    /// Queue work until a component becomes available.
    /// Returns a task that completes when the work can be executed.
    /// </summary>
    public async Task<RouteResult> RouteWithWaitAsync(
        string[] fallbackChain,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        // First try immediate routing
        var immediate = await RouteAsync(fallbackChain, ct);
        if (immediate.Success)
            return immediate;

        // No component ready - queue the work and wait
        var primary = fallbackChain.FirstOrDefault();
        if (string.IsNullOrEmpty(primary))
        {
            return new RouteResult
            {
                Success = false,
                PendingReason = "No components in fallback chain"
            };
        }

        // Ensure models are downloading for the primary component
        if (_componentStates.TryGetValue(primary, out var state))
        {
            foreach (var modelId in state.Registration.RequiredModels)
            {
                // Start download in background (fire and forget, but track)
                _ = _downloader.EnsureModelAsync(modelId, ct);
            }
        }

        // Wait for a component to become ready
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var readySignal = await _signalSink.WaitForAsync(
                s => s.SignalType == CapabilitySignalType.ComponentReady &&
                     fallbackChain.Contains(s.ComponentId),
                effectiveTimeout,
                linkedCts.Token);

            return new RouteResult
            {
                Success = true,
                SelectedComponent = readySignal.ComponentId,
                WaitedForAvailability = true
            };
        }
        catch (TimeoutException)
        {
            return new RouteResult
            {
                Success = false,
                PendingReason = $"Timed out waiting for components: {string.Join(", ", fallbackChain)}"
            };
        }
    }

    /// <summary>
    /// Get the best execution provider for a model.
    /// </summary>
    public async Task<string> GetBestProviderAsync(string modelId, CancellationToken ct = default)
    {
        var model = ModelManifest.Instance.GetModel(modelId);
        if (model == null)
            return "CPUExecutionProvider";

        var capabilities = await _registry.GetCapabilitiesAsync(ct);

        foreach (var provider in model.PreferredProviders)
        {
            if (capabilities.HasProvider(provider))
                return provider;
        }

        return "CPUExecutionProvider";
    }

    /// <summary>
    /// Activate a component - ensures all required models are downloaded.
    /// </summary>
    public async Task<ActivationResult> ActivateComponentAsync(
        string componentId,
        CancellationToken ct = default)
    {
        if (!_componentStates.TryGetValue(componentId, out var state))
        {
            return new ActivationResult
            {
                Success = false,
                Error = $"Component {componentId} not registered"
            };
        }

        state.Status = ComponentStatus.Activating;

        await _signalSink.EmitAsync(new CapabilitySignal
        {
            SignalType = CapabilitySignalType.ComponentActivated,
            ComponentId = componentId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("Activating component {ComponentId}", componentId);

        // Ensure all required models
        var results = await _downloader.EnsureModelsForComponentAsync(componentId, ct);
        var failures = results.Where(r => !r.Success).ToList();

        if (failures.Any())
        {
            state.Status = ComponentStatus.Failed;

            await _signalSink.EmitAsync(new CapabilitySignal
            {
                SignalType = CapabilitySignalType.ComponentError,
                ComponentId = componentId,
                Error = $"Failed to download models: {string.Join(", ", failures.Select(f => f.ModelId))}",
                Timestamp = DateTimeOffset.UtcNow
            }, ct);

            return new ActivationResult
            {
                Success = false,
                Error = $"Failed to download: {string.Join(", ", failures.Select(f => $"{f.ModelId}: {f.Error}"))}"
            };
        }

        state.Status = ComponentStatus.Ready;
        state.BecameReadyAt = DateTimeOffset.UtcNow;

        await _signalSink.EmitAsync(new CapabilitySignal
        {
            SignalType = CapabilitySignalType.ComponentReady,
            ComponentId = componentId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("Component {ComponentId} activated and ready", componentId);

        return new ActivationResult
        {
            Success = true,
            DownloadedModels = results.Where(r => !r.AlreadyExisted).Select(r => r.ModelId).ToList()
        };
    }

    private void OnSignalReceived(CapabilitySignal signal)
    {
        switch (signal.SignalType)
        {
            case CapabilitySignalType.ModelAvailable:
                // Check if any waiting components can now be activated
                _ = CheckWaitingComponentsAsync(signal.ModelId);
                break;

            case CapabilitySignalType.ProviderChanged:
            case CapabilitySignalType.GpuAvailable:
                // Re-evaluate all components
                _ = ReEvaluateAllComponentsAsync();
                break;
        }
    }

    private async Task CheckWaitingComponentsAsync(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return;

        foreach (var (componentId, state) in _componentStates)
        {
            if (state.Status == ComponentStatus.Initializing ||
                state.Status == ComponentStatus.WaitingForModels)
            {
                if (state.Registration.RequiredModels.Contains(modelId))
                {
                    await IsComponentReadyAsync(componentId);
                }
            }
        }
    }

    private async Task ReEvaluateAllComponentsAsync()
    {
        foreach (var componentId in _componentStates.Keys)
        {
            await IsComponentReadyAsync(componentId);
        }
    }

    private async Task ProcessPendingWorkAsync(string componentId, CancellationToken ct)
    {
        if (!_pendingWork.TryGetValue(componentId, out var queue))
            return;

        while (queue.TryDequeue(out var pending))
        {
            pending.TaskCompletionSource.TrySetResult(componentId);
        }

        await Task.CompletedTask;
    }

    private string GetPendingReason(string componentId)
    {
        if (!_componentStates.TryGetValue(componentId, out var state))
            return "Component not registered";

        var availability = _downloader.CheckModelAvailability();
        var missingModels = state.Registration.RequiredModels
            .Where(m => !availability.GetValueOrDefault(m, false))
            .ToList();

        if (missingModels.Any())
            return $"Waiting for models: {string.Join(", ", missingModels)}";

        return state.Status.ToString();
    }

    public void Dispose()
    {
        _signalSubscription.Dispose();
    }
}

/// <summary>
/// Component registration with requirements.
/// </summary>
public record ComponentRegistration
{
    public required string ComponentId { get; init; }
    public List<string> RequiredModels { get; init; } = [];
    public List<string> RequiredProviders { get; init; } = [];
    public List<string> PreferredProviders { get; init; } = [];
    public bool RequiresGpu { get; init; }
    public long? MinimumMemoryBytes { get; init; }
}

/// <summary>
/// Component state tracking.
/// </summary>
public class ComponentState
{
    public required ComponentRegistration Registration { get; init; }
    public ComponentStatus Status { get; set; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset? BecameReadyAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Component status enum.
/// </summary>
public enum ComponentStatus
{
    Initializing,
    WaitingForModels,
    Activating,
    Ready,
    Busy,
    Failed,
    Disabled
}

/// <summary>
/// Result of routing work.
/// </summary>
public record RouteResult
{
    public bool Success { get; init; }
    public string? SelectedComponent { get; init; }
    public bool IsFallback { get; init; }
    public bool WaitedForAvailability { get; init; }
    public string? PendingReason { get; init; }
}

/// <summary>
/// Result of activating a component.
/// </summary>
public record ActivationResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<string> DownloadedModels { get; init; } = [];
}

/// <summary>
/// Pending work item.
/// </summary>
internal class PendingWork
{
    public TaskCompletionSource<string> TaskCompletionSource { get; } = new();
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;
}

using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
///     Central capability coordinator - the shared node for all coordinators.
///     Provides unified access to capability detection, model management, and routing.
///     This is the "config signal" that all coordinators share.
/// </summary>
public class CapabilityCoordinator : IDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<CapabilityCoordinator> _logger;

    private bool _initialized;

    public CapabilityCoordinator(
        ILogger<CapabilityCoordinator> logger,
        CapabilityRegistry registry,
        BackgroundModelDownloader downloader,
        CapabilityRouter router,
        ICapabilitySignalSink signalSink)
    {
        _logger = logger;
        Registry = registry;
        Downloader = downloader;
        Router = router;
        SignalSink = signalSink;
    }

    /// <summary>
    ///     Get the capability registry for querying hardware/provider info.
    /// </summary>
    public CapabilityRegistry Registry { get; }

    /// <summary>
    ///     Get the model downloader for requesting models.
    /// </summary>
    public BackgroundModelDownloader Downloader { get; }

    /// <summary>
    ///     Get the router for work distribution.
    /// </summary>
    public CapabilityRouter Router { get; }

    /// <summary>
    ///     Get the signal sink for pub/sub.
    /// </summary>
    public ICapabilitySignalSink SignalSink { get; }

    public void Dispose()
    {
        _initLock.Dispose();
        Router.Dispose();
        Downloader.Dispose();
        Registry.Dispose();
        (SignalSink as IDisposable)?.Dispose();
    }

    /// <summary>
    ///     Initialize the capability system - detects hardware and checks model availability.
    ///     Call this once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing capability coordinator...");

            // Detect hardware capabilities
            var capabilities = await Registry.GetCapabilitiesAsync(ct);

            _logger.LogInformation(
                "Detected: Platform={Platform}, GPU={Gpu} ({Backend}), Provider={Provider}, Processors={Processors}",
                capabilities.Platform.OsDescription,
                capabilities.Gpu.IsAvailable,
                capabilities.Gpu.Backend,
                capabilities.PreferredProvider,
                capabilities.Platform.ProcessorCount);

            // Check which models are already available
            var modelAvailability = Downloader.CheckModelAvailability();
            var availableModels = modelAvailability.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            var missingModels = modelAvailability.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();

            _logger.LogInformation("Models available: {Available}, missing: {Missing}",
                availableModels.Count, missingModels.Count);

            // Emit initialization signal
            await SignalSink.EmitAsync(new CapabilitySignal
            {
                SignalType = CapabilitySignalType.CapabilitiesRefreshed,
                Timestamp = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["gpu_available"] = capabilities.Gpu.IsAvailable,
                    ["provider"] = capabilities.PreferredProvider,
                    ["models_available"] = availableModels.Count,
                    ["models_missing"] = missingModels.Count
                }
            }, ct);

            _initialized = true;
            _logger.LogInformation("Capability coordinator initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Register a wave/component with its requirements.
    /// </summary>
    public void RegisterWave(string waveId, params string[] requiredModels)
    {
        Router.RegisterComponent(new ComponentRegistration
        {
            ComponentId = waveId,
            RequiredModels = requiredModels.ToList()
        });
    }

    /// <summary>
    ///     Activate a wave - downloads required models and marks as ready.
    /// </summary>
    public Task<ActivationResult> ActivateWaveAsync(string waveId, CancellationToken ct = default)
    {
        return Router.ActivateComponentAsync(waveId, ct);
    }

    /// <summary>
    ///     Check if a wave is ready to process.
    /// </summary>
    public Task<bool> IsWaveReadyAsync(string waveId, CancellationToken ct = default)
    {
        return Router.IsComponentReadyAsync(waveId, ct);
    }

    /// <summary>
    ///     Get the best execution provider for running ONNX models.
    /// </summary>
    public Task<string> GetBestProviderAsync(string modelId, CancellationToken ct = default)
    {
        return Router.GetBestProviderAsync(modelId, ct);
    }

    /// <summary>
    ///     Ensure a model is available, downloading if necessary.
    /// </summary>
    public Task<ModelDownloadResult> EnsureModelAsync(string modelId, CancellationToken ct = default)
    {
        return Downloader.EnsureModelAsync(modelId, ct);
    }

    /// <summary>
    ///     Route work with fallback chain.
    /// </summary>
    public Task<RouteResult> RouteWorkAsync(string[] fallbackChain, CancellationToken ct = default)
    {
        return Router.RouteAsync(fallbackChain, ct);
    }

    /// <summary>
    ///     Wait for the best available component in a fallback chain.
    /// </summary>
    public Task<RouteResult> RouteWorkWithWaitAsync(
        string[] fallbackChain,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        return Router.RouteWithWaitAsync(fallbackChain, timeout, ct);
    }

    /// <summary>
    ///     Subscribe to capability changes.
    /// </summary>
    public IDisposable OnCapabilityChange(Action<CapabilitySignal> handler)
    {
        return SignalSink.Subscribe(handler);
    }

    /// <summary>
    ///     Subscribe to specific signal types.
    /// </summary>
    public IDisposable OnSignal(CapabilitySignalType signalType, Action<CapabilitySignal> handler)
    {
        return SignalSink.Subscribe(handler, s => s.SignalType == signalType);
    }

    /// <summary>
    ///     Get current capability snapshot.
    /// </summary>
    public Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        return Registry.GetCapabilitiesAsync(ct);
    }

    /// <summary>
    ///     Get model path if available.
    /// </summary>
    public string? GetModelPath(string modelId)
    {
        var model = ModelManifest.Instance.GetModel(modelId);
        if (model == null) return null;

        var modelsDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(modelsDir, "lucidrag", "models", model.RelativePath);

        return File.Exists(path) ? path : null;
    }
}
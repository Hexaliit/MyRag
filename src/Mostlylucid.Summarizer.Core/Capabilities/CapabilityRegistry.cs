using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
///     Central registry for hardware capabilities and model availability.
///     Detected once at startup and cached as signals for all coordinators.
///     Uses reflection to detect ONNX Runtime providers without compile-time dependency.
/// </summary>
public class CapabilityRegistry : IDisposable
{
    // ONNX detection via reflection
    private static Type? _ortEnvType;
    private static MethodInfo? _getProvidersMethod;
    private static bool _onnxDetectionAttempted;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<CapabilityRegistry> _logger;

    // Cached capability signals
    private CapabilitySnapshot? _snapshot;

    public CapabilityRegistry(ILogger<CapabilityRegistry> logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        _initLock.Dispose();
    }

    /// <summary>
    ///     Get the current capability snapshot. Initializes on first access.
    /// </summary>
    public async Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        if (_snapshot != null)
            return _snapshot;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_snapshot != null)
                return _snapshot;

            _snapshot = await DetectCapabilitiesAsync(ct);
            return _snapshot;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Force re-detection of capabilities (e.g., after model download).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            _snapshot = await DetectCapabilitiesAsync(ct);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<CapabilitySnapshot> DetectCapabilitiesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Detecting hardware capabilities...");

        var providers = DetectExecutionProviders();
        var gpu = await DetectGpuCapabilitiesAsync(providers, ct);
        var platform = DetectPlatformInfo();

        var snapshot = new CapabilitySnapshot
        {
            DetectedAt = DateTimeOffset.UtcNow,
            Platform = platform,
            Gpu = gpu,
            ExecutionProviders = providers,
            PreferredProvider = SelectPreferredProvider(providers, gpu),
            Models = new Dictionary<string, ModelStatus>()
        };

        _logger.LogInformation(
            "Capabilities detected: Platform={Platform}, GPU={GpuAvailable}, Provider={Provider}",
            platform.OsDescription,
            gpu.IsAvailable,
            snapshot.PreferredProvider);

        return snapshot;
    }

    private Task<GpuCapabilities> DetectGpuCapabilitiesAsync(List<string> providers, CancellationToken ct)
    {
        var gpu = new GpuCapabilities();

        try
        {
            // Check CUDA
            if ((RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) &&
                providers.Contains("CUDAExecutionProvider"))
            {
                gpu = new GpuCapabilities
                {
                    IsAvailable = true,
                    Backend = GpuBackend.Cuda,
                    DeviceName = "CUDA Device",
                    DriverVersion = Environment.GetEnvironmentVariable("CUDA_VERSION")
                };
                _logger.LogInformation("CUDA detected and available");
            }
            // Check DirectML on Windows
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                     providers.Contains("DmlExecutionProvider"))
            {
                gpu = new GpuCapabilities
                {
                    IsAvailable = true,
                    Backend = GpuBackend.DirectML,
                    DeviceName = "DirectML Device"
                };
                _logger.LogInformation("DirectML detected and available");
            }
            // Check CoreML on macOS
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                     providers.Contains("CoreMLExecutionProvider"))
            {
                gpu = new GpuCapabilities
                {
                    IsAvailable = true,
                    Backend = GpuBackend.CoreML,
                    DeviceName = "CoreML/Metal"
                };
                _logger.LogInformation("CoreML/Metal detected and available");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPU detection failed, falling back to CPU");
        }

        return Task.FromResult(gpu);
    }

    /// <summary>
    ///     Detect available ONNX execution providers using reflection.
    ///     This allows detection without a compile-time dependency on ONNX Runtime.
    /// </summary>
    private List<string> DetectExecutionProviders()
    {
        try
        {
            // Try to find ONNX Runtime via reflection
            if (!_onnxDetectionAttempted)
            {
                _onnxDetectionAttempted = true;
                TryLoadOnnxTypes();
            }

            if (_ortEnvType != null && _getProvidersMethod != null)
            {
                // Get OrtEnv.Instance()
                var instanceProperty = _ortEnvType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var envInstance = instanceProperty?.GetValue(null);

                if (envInstance != null)
                {
                    // Call GetAvailableProviders()
                    var result = _getProvidersMethod.Invoke(envInstance, null);
                    if (result is IEnumerable<string> providers)
                    {
                        var list = providers.ToList();
                        _logger.LogDebug("Detected ONNX providers: {Providers}", string.Join(", ", list));
                        return list;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ONNX Runtime not available or failed to detect providers");
        }

        // Default to CPU only if ONNX not available
        _logger.LogInformation("ONNX Runtime not detected, defaulting to CPU provider");
        return ["CPUExecutionProvider"];
    }

    private void TryLoadOnnxTypes()
    {
        try
        {
            // Try to load the ONNX Runtime assembly
            var onnxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.ML.OnnxRuntime");

            if (onnxAssembly == null)
                // Try to load it explicitly
                try
                {
                    onnxAssembly = Assembly.Load("Microsoft.ML.OnnxRuntime");
                }
                catch
                {
                    // Not available
                    return;
                }

            _ortEnvType = onnxAssembly.GetType("Microsoft.ML.OnnxRuntime.OrtEnv");
            if (_ortEnvType != null) _getProvidersMethod = _ortEnvType.GetMethod("GetAvailableProviders");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load ONNX Runtime types");
        }
    }

    private PlatformInfo DetectPlatformInfo()
    {
        return new PlatformInfo
        {
            OsDescription = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Is64Bit = Environment.Is64BitProcess
        };
    }

    private string SelectPreferredProvider(List<string> providers, GpuCapabilities gpu)
    {
        // Priority: CUDA > DirectML > CoreML > CPU
        if (gpu.IsAvailable)
            return gpu.Backend switch
            {
                GpuBackend.Cuda when providers.Contains("CUDAExecutionProvider") => "CUDAExecutionProvider",
                GpuBackend.DirectML when providers.Contains("DmlExecutionProvider") => "DmlExecutionProvider",
                GpuBackend.CoreML when providers.Contains("CoreMLExecutionProvider") => "CoreMLExecutionProvider",
                _ => "CPUExecutionProvider"
            };

        return "CPUExecutionProvider";
    }
}

/// <summary>
///     Immutable snapshot of system capabilities.
/// </summary>
public record CapabilitySnapshot
{
    public DateTimeOffset DetectedAt { get; init; }
    public required PlatformInfo Platform { get; init; }
    public required GpuCapabilities Gpu { get; init; }
    public required List<string> ExecutionProviders { get; init; }
    public required string PreferredProvider { get; init; }
    public required Dictionary<string, ModelStatus> Models { get; init; }

    /// <summary>
    ///     Check if GPU acceleration is available.
    /// </summary>
    public bool HasGpu => Gpu.IsAvailable;

    /// <summary>
    ///     Check if a specific execution provider is available.
    /// </summary>
    public bool HasProvider(string provider)
    {
        return ExecutionProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Get the best available provider for a given preference.
    /// </summary>
    public string GetBestProvider(params string[] preferences)
    {
        foreach (var pref in preferences)
            if (HasProvider(pref))
                return pref;
        return PreferredProvider;
    }
}

/// <summary>
///     GPU hardware capabilities.
/// </summary>
public record GpuCapabilities
{
    public bool IsAvailable { get; init; }
    public GpuBackend Backend { get; init; } = GpuBackend.None;
    public string? DeviceName { get; init; }
    public string? DriverVersion { get; init; }
    public long? MemoryBytes { get; init; }
    public int? ComputeCapabilityMajor { get; init; }
    public int? ComputeCapabilityMinor { get; init; }
}

/// <summary>
///     GPU backend types.
/// </summary>
public enum GpuBackend
{
    None,
    Cuda,
    DirectML,
    CoreML,
    ROCm,
    Vulkan
}

/// <summary>
///     Platform information.
/// </summary>
public record PlatformInfo
{
    public required string OsDescription { get; init; }
    public required string Architecture { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required string FrameworkDescription { get; init; }
    public int ProcessorCount { get; init; }
    public bool Is64Bit { get; init; }
}

/// <summary>
///     Status of a specific model.
/// </summary>
public record ModelStatus
{
    public required string ModelId { get; init; }
    public bool IsDownloaded { get; init; }
    public bool IsLoaded { get; init; }
    public string? LocalPath { get; init; }
    public long? FileSizeBytes { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}
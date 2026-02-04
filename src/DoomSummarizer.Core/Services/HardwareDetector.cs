using System.Runtime.InteropServices;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Probes hardware capabilities to resolve the "dynamic" profile.
/// </summary>
public static class HardwareDetector
{
    /// <summary>
    ///     Detect hardware capabilities: RAM, CPU, GPU, Ollama availability, cloud keys.
    /// </summary>
    public static async Task<HardwareCapabilities> DetectAsync(CancellationToken ct = default)
    {
        var ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var cpuCores = Environment.ProcessorCount;
        var isArm = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;
        var hasGpu = DetectGpu();
        var ollamaAvailable = await CheckOllamaAsync(ct);
        var hasCloudKeys = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        return new HardwareCapabilities
        {
            TotalRamBytes = ramBytes,
            CpuCores = cpuCores,
            HasGpu = hasGpu,
            IsArm = isArm,
            OllamaAvailable = ollamaAvailable,
            HasCloudKeys = hasCloudKeys
        };
    }

    /// <summary>
    ///     Resolve a profile name from hardware capabilities.
    /// </summary>
    public static async Task<string> ResolveProfileAsync(CancellationToken ct = default)
    {
        var hw = await DetectAsync(ct);
        return ResolveProfile(hw);
    }

    /// <summary>
    ///     Resolve a profile name from known hardware capabilities.
    ///     GPU presence selects "desktop" (enables GPU offload for LLamaSharp).
    ///     Decision table:
    ///     RAM ≤ 4GB                      → pi        (CPU-only, tiny models)
    ///     RAM ≤ 16GB + GPU               → desktop   (GPU-accelerated local LLM)
    ///     RAM ≤ 16GB                     → laptop    (CPU-only local LLM)
    ///     RAM ≤ 64GB + GPU               → desktop   (GPU-accelerated local LLM)
    ///     RAM ≤ 64GB + Ollama (no GPU)   → server    (Ollama-primary, no local LLM)
    ///     RAM ≤ 64GB                     → laptop    (CPU-only local LLM)
    ///     RAM > 64GB + Ollama + cloud    → enterprise
    ///     RAM > 64GB + Ollama            → server
    ///     RAM > 64GB                     → server
    /// </summary>
    public static string ResolveProfile(HardwareCapabilities hw)
    {
        var ramGb = hw.TotalRamGb;

        if (ramGb <= 4)
            return "pi";

        // GPU presence always selects "desktop" (enables GPU offload)
        if (ramGb <= 64 && hw.HasGpu)
            return "desktop";

        if (ramGb <= 16)
            return "laptop";

        // 16-64GB without GPU: prefer Ollama if available, else laptop
        if (ramGb <= 64 && hw.OllamaAvailable)
            return "server";

        if (ramGb <= 64)
            return "laptop";

        // >64GB: enterprise with cloud keys + Ollama, otherwise server
        if (hw.OllamaAvailable)
            return hw.HasCloudKeys ? "enterprise" : "server";

        return "server";
    }

    private static bool DetectGpu()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DetectGpuWindows();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return DetectGpuLinux();

        // macOS: Metal is always available on Apple Silicon
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return true;

        return false;
    }

    /// <summary>
    ///     Windows GPU detection via driver DLLs in System32.
    ///     NVIDIA drivers always install nvcuda.dll (CUDA runtime), even without the full CUDA Toolkit.
    ///     AMD discrete GPUs install amdxc64.dll (DirectX compiler) and amdvlk64.dll (Vulkan ICD).
    ///     Intel Arc discrete GPUs install ze_loader.dll (Level Zero compute API).
    ///     Falls back to CUDA_PATH/CUDA_HOME env vars as last resort.
    /// </summary>
    private static bool DetectGpuWindows()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // NVIDIA: CUDA driver is always installed with GeForce/Quadro/Tesla drivers
        if (File.Exists(Path.Combine(system32, "nvcuda.dll")))
            return true;

        // AMD: DirectX shader compiler present with Radeon drivers (discrete GPUs)
        if (File.Exists(Path.Combine(system32, "amdxc64.dll")))
            return true;

        // AMD: Vulkan ICD for AMD discrete GPUs
        if (File.Exists(Path.Combine(system32, "amdvlk64.dll")))
            return true;

        // Intel Arc: Level Zero runtime for discrete Intel GPUs
        if (File.Exists(Path.Combine(system32, "ze_loader.dll")))
            return true;

        // Fallback: CUDA Toolkit environment variables
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_PATH"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_HOME")))
            return true;

        return false;
    }

    /// <summary>
    ///     Linux GPU detection via device nodes and driver paths.
    ///     NVIDIA: /dev/nvidia0 device node (created by nvidia kernel module).
    ///     AMD ROCm: /dev/kfd device (Kernel Fusion Driver for AMD compute GPUs).
    ///     Falls back to CUDA_PATH env var.
    /// </summary>
    private static bool DetectGpuLinux()
    {
        // NVIDIA: kernel module creates /dev/nvidia0
        if (File.Exists("/dev/nvidia0"))
            return true;

        // AMD ROCm: Kernel Fusion Driver for GPU compute
        if (File.Exists("/dev/kfd"))
            return true;

        // Fallback: CUDA Toolkit env var
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_PATH")))
            return true;

        return false;
    }

    private static async Task<bool> CheckOllamaAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await client.GetAsync("http://localhost:11434/api/tags", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
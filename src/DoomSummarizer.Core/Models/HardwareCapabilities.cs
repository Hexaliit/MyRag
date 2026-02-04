namespace DoomSummarizer.Models;

/// <summary>
///     Detected hardware capabilities for dynamic profile resolution.
/// </summary>
public record HardwareCapabilities
{
    /// <summary>Total physical RAM in bytes.</summary>
    public long TotalRamBytes { get; init; }

    /// <summary>Total RAM in GB (convenience).</summary>
    public double TotalRamGb => TotalRamBytes / (1024.0 * 1024.0 * 1024.0);

    /// <summary>Number of logical CPU cores.</summary>
    public int CpuCores { get; init; }

    /// <summary>Whether a dedicated GPU was detected.</summary>
    public bool HasGpu { get; init; }

    /// <summary>Whether the CPU is ARM architecture.</summary>
    public bool IsArm { get; init; }

    /// <summary>Whether an Ollama server is reachable.</summary>
    public bool OllamaAvailable { get; init; }

    /// <summary>Whether cloud API keys are configured (any of OPENAI_API_KEY, ANTHROPIC_API_KEY).</summary>
    public bool HasCloudKeys { get; init; }
}
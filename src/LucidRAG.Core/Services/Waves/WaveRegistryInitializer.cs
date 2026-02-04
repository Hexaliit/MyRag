using Microsoft.Extensions.Hosting;

namespace LucidRAG.Services.Waves;

/// <summary>
///     Background service that initializes the wave registry on startup.
/// </summary>
public sealed class WaveRegistryInitializer : IHostedService
{
    private readonly ILogger<WaveRegistryInitializer> _logger;
    private readonly IWaveRegistry _registry;

    public WaveRegistryInitializer(
        IWaveRegistry registry,
        ILogger<WaveRegistryInitializer> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Initializing wave registry...");
        await _registry.InitializeAsync(ct);
        _logger.LogInformation("Wave registry ready with {Count} wave(s)", _registry.AvailableWaves.Count);
    }

    public Task StopAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
/// DI registration for capability system.
/// </summary>
public static class CapabilityServiceExtensions
{
    /// <summary>
    /// Add the capability system to DI.
    /// This provides:
    /// - CapabilityRegistry: Hardware/provider detection
    /// - BackgroundModelDownloader: Lazy model downloads
    /// - CapabilityRouter: Work routing based on capabilities
    /// - ICapabilitySignalSink: Pub/sub for capability changes
    /// - CapabilityCoordinator: Unified access point for all coordinators
    /// </summary>
    public static IServiceCollection AddCapabilitySystem(
        this IServiceCollection services,
        string? modelsDirectory = null)
    {
        // Signal sink is singleton - shared across all coordinators
        services.AddSingleton<ICapabilitySignalSink, CapabilitySignalSink>();

        // Registry is singleton - capabilities don't change often
        services.AddSingleton<CapabilityRegistry>();

        // Downloader is singleton - manages downloads globally
        services.AddSingleton<BackgroundModelDownloader>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackgroundModelDownloader>>();
            var registry = sp.GetRequiredService<CapabilityRegistry>();
            var signalSink = sp.GetRequiredService<ICapabilitySignalSink>();
            return new BackgroundModelDownloader(logger, registry, signalSink, modelsDirectory);
        });

        // Router is singleton - routes work globally
        services.AddSingleton<CapabilityRouter>();

        // Coordinator is singleton - the shared config signal node
        services.AddSingleton<CapabilityCoordinator>();

        return services;
    }

    /// <summary>
    /// Initialize the capability system at startup.
    /// Call this after building the service provider.
    /// </summary>
    public static async Task InitializeCapabilitySystemAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        var coordinator = services.GetRequiredService<CapabilityCoordinator>();
        await coordinator.InitializeAsync(ct);
    }
}

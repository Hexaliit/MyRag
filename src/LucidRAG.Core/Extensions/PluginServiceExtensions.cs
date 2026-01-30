using System.Reflection;
using LucidRAG.Coordination;
using LucidRAG.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.Extensions;

/// <summary>
/// DI extension methods for the unified plugin system.
/// </summary>
public static class PluginServiceExtensions
{
    /// <summary>
    /// Register a compile-time plugin. Scans the assembly for IWave implementations,
    /// loads embedded wave manifests, and registers prompts/lenses.
    /// </summary>
    public static IServiceCollection AddPlugin<TPlugin>(
        this IServiceCollection services,
        IConfiguration? configuration = null)
        where TPlugin : class, IPlugin, new()
    {
        var plugin = new TPlugin();
        var assembly = typeof(TPlugin).Assembly;

        services.AddSingleton<IPlugin>(plugin);

        if (configuration is not null)
            plugin.ConfigureServices(services, configuration);

        RegisterWavesFromAssembly(services, assembly);

        return services;
    }

    /// <summary>
    /// Register all IWave implementations found in the given assembly.
    /// </summary>
    public static IServiceCollection RegisterWavesFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        var waveTypes = WaveDiscovery.ScanForWaveTypes(assembly);
        foreach (var waveType in waveTypes)
            services.AddSingleton(typeof(IWave), waveType);

        return services;
    }

    /// <summary>
    /// Register plugins from a directory containing plugin DLLs.
    /// </summary>
    public static IServiceCollection AddRuntimePlugins(
        this IServiceCollection services,
        string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return services;

        foreach (var dllPath in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories))
            services.AddRuntimePlugin(dllPath);

        return services;
    }

    /// <summary>
    /// Register a single runtime-loaded plugin from a DLL path.
    /// </summary>
    public static IServiceCollection AddRuntimePlugin(
        this IServiceCollection services,
        string dllPath)
    {
        services.AddSingleton<IWave>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<RuntimePluginLoader>>();
            return new RuntimePluginProxy(dllPath, logger);
        });

        return services;
    }

    /// <summary>
    /// Register the core coordination services (DocumentCoordinator + dependencies).
    /// </summary>
    public static IServiceCollection AddLucidRagCoordination(this IServiceCollection services)
    {
        services.AddSingleton<ICoordinator, DocumentCoordinator>();
        return services;
    }
}

internal sealed class RuntimePluginLoader;

/// <summary>
/// Proxy wave that lazily loads a plugin DLL on first execution.
/// </summary>
internal sealed class RuntimePluginProxy : IWave
{
    private readonly string _dllPath;
    private readonly ILogger _logger;
    private IWave? _inner;
    private bool _loaded;

    public RuntimePluginProxy(string dllPath, ILogger logger)
    {
        _dllPath = dllPath;
        _logger = logger;
        Name = Path.GetFileNameWithoutExtension(dllPath);
    }

    public string Name { get; }

    public async Task<IReadOnlyList<Signal>> ExecuteAsync(
        WaveContext context, CancellationToken ct = default)
    {
        EnsureLoaded();
        return _inner is not null ? await _inner.ExecuteAsync(context, ct) : [];
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var loadContext = new DoomSummarizer.Plugins.Runtime.PluginLoadContext(_dllPath);
            var assembly = loadContext.LoadFromAssemblyPath(_dllPath);
            var waveTypes = WaveDiscovery.ScanForWaveTypes(assembly);
            if (waveTypes.Count > 0)
                _inner = (IWave?)Activator.CreateInstance(waveTypes[0]);

            _logger.LogInformation("Loaded runtime plugin: {Path} ({Waves} waves)", _dllPath, waveTypes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load runtime plugin: {Path}", _dllPath);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Key-based registry for source plugins.
///     Maps one or more string keys to each <see cref="ISourcePlugin" />.
///     Thread-safe for concurrent reads after registration completes.
/// </summary>
public sealed class SourcePluginRegistry
{
    private readonly List<ISourcePlugin> _all = [];
    private readonly ConcurrentDictionary<string, ISourcePlugin> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered keys (sorted for deterministic output).</summary>
    public IReadOnlyList<string> AllKeys => _byKey.Keys.OrderBy(k => k).ToList();

    /// <summary>All registered plugins (in registration order).</summary>
    public IReadOnlyList<ISourcePlugin> All => _all;

    /// <summary>
    ///     Register a plugin for all its declared keys.
    /// </summary>
    public void Register(ISourcePlugin plugin)
    {
        _all.Add(plugin);
        foreach (var key in plugin.Metadata.Keys)
            if (!_byKey.TryAdd(key.ToLowerInvariant(), plugin))
                Debug.WriteLine(
                    $"[PluginRegistry] Warning: duplicate key '{key}' — skipping (already registered by {_byKey[key.ToLowerInvariant()].Metadata.DisplayName})");
    }

    /// <summary>
    ///     Find a plugin by key. Returns null if no plugin handles this key.
    /// </summary>
    public ISourcePlugin? FindByKey(string key)
    {
        return _byKey.GetValueOrDefault(key.ToLowerInvariant());
    }

    /// <summary>
    ///     Find plugins that match a capability filter.
    /// </summary>
    public IReadOnlyList<ISourcePlugin> FindByCapability(SourceCapabilities capability)
    {
        return _all.Where(p => p.Metadata.Capabilities.HasFlag(capability)).ToList();
    }

    /// <summary>
    ///     Find plugins that require no API keys.
    /// </summary>
    public IReadOnlyList<ISourcePlugin> FindFreePlugins()
    {
        return _all.Where(p => p.Metadata.RequiredApiKeys.Count == 0).ToList();
    }

    /// <summary>
    ///     Check if a key is registered.
    /// </summary>
    public bool HasKey(string key)
    {
        return _byKey.ContainsKey(key.ToLowerInvariant());
    }

    /// <summary>
    ///     Initialize all registered plugins.
    ///     Plugins that fail initialization are logged but not removed.
    /// </summary>
    public async Task InitializeAllAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        foreach (var plugin in _all)
            try
            {
                await plugin.InitializeAsync(services, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginRegistry] Failed to initialize {plugin.Metadata.DisplayName}: {ex.Message}");
            }
    }
}

/// <summary>
///     Key-based registry for output plugins.
/// </summary>
public sealed class OutputPluginRegistry
{
    private readonly List<IOutputPlugin> _all = [];
    private readonly ConcurrentDictionary<string, IOutputPlugin> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered output plugins.</summary>
    public IReadOnlyList<IOutputPlugin> All => _all;

    /// <summary>Register an output plugin for all its declared keys.</summary>
    public void Register(IOutputPlugin plugin)
    {
        _all.Add(plugin);
        foreach (var key in plugin.Metadata.Keys)
            if (!_byKey.TryAdd(key.ToLowerInvariant(), plugin))
                Debug.WriteLine($"[OutputRegistry] Warning: duplicate key '{key}'");
    }

    /// <summary>Find an output plugin by key.</summary>
    public IOutputPlugin? FindByKey(string key)
    {
        return _byKey.GetValueOrDefault(key.ToLowerInvariant());
    }

    /// <summary>Initialize all registered output plugins.</summary>
    public async Task InitializeAllAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        foreach (var plugin in _all)
            try
            {
                await plugin.InitializeAsync(services, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputRegistry] Failed to initialize {plugin.Metadata.DisplayName}: {ex.Message}");
            }
    }
}
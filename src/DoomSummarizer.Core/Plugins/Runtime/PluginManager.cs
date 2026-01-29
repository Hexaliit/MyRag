using System.Diagnostics;

namespace DoomSummarizer.Plugins.Runtime;

/// <summary>
/// Orchestrates plugin lifecycle: install, uninstall, list, enable/disable.
/// Manages the manifest and delegates download to <see cref="PluginLoader"/>.
/// </summary>
public sealed class PluginManager
{
    private readonly PluginLoader _loader;
    private readonly PluginManifest _manifest;

    public PluginManager(HttpClient? httpClient = null)
    {
        _loader = new PluginLoader(httpClient);
        _manifest = PluginManifest.Load();
    }

    /// <summary>
    /// Install a plugin by shorthand or full NuGet package ID.
    /// Supports:
    ///   - Shorthand: "source-imap" → Mostlylucid.DoomSummarizer.Source.Imap
    ///   - Full package: "Acme.DoomSummarizer.Source.Foo"
    ///   - With version: "Acme.DoomSummarizer.Source.Foo" + "2.1.0"
    /// </summary>
    public async Task<bool> InstallAsync(string input, string? version = null, Action<string>? progress = null)
    {
        var shorthand = PluginManifest.KnownShorthands.ContainsKey(input) ? input : null;
        var packageId = PluginManifest.ResolvePackageId(input);

        // Check if already installed
        var existing = _manifest.Find(packageId);
        if (existing != null)
        {
            if (version == null || existing.Version == version)
            {
                progress?.Invoke($"{packageId} {existing.Version} is already installed.");
                return true;
            }
            progress?.Invoke($"Upgrading {packageId} from {existing.Version} to {version}...");
        }

        var installDir = await _loader.DownloadPackageAsync(packageId, version, progress);
        if (installDir == null)
            return false;

        // Resolve actual version from directory name
        var actualVersion = version ?? Path.GetFileName(installDir);

        if (existing != null)
        {
            existing.Version = actualVersion;
            existing.InstallPath = installDir;
            existing.Enabled = true;
        }
        else
        {
            _manifest.Plugins.Add(new PluginEntry
            {
                PackageId = packageId,
                Version = actualVersion,
                InstallPath = installDir,
                Shorthand = shorthand,
                Enabled = true
            });
        }

        _manifest.Save();
        progress?.Invoke($"Installed {packageId} {actualVersion}");
        return true;
    }

    /// <summary>Uninstall a plugin by shorthand or package ID.</summary>
    public bool Uninstall(string input, Action<string>? progress = null)
    {
        var packageId = PluginManifest.ResolvePackageId(input);
        var entry = _manifest.Find(packageId);

        if (entry == null)
        {
            progress?.Invoke($"{packageId} is not installed.");
            return false;
        }

        // Remove install directory
        if (entry.InstallPath != null && Directory.Exists(entry.InstallPath))
        {
            try
            {
                Directory.Delete(entry.InstallPath, recursive: true);

                // Clean up parent directory if empty
                var parent = Path.GetDirectoryName(entry.InstallPath);
                if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginManager] Failed to delete {entry.InstallPath}: {ex.Message}");
            }
        }

        _manifest.Plugins.Remove(entry);
        _manifest.Save();
        progress?.Invoke($"Uninstalled {packageId}");
        return true;
    }

    /// <summary>Enable a previously disabled plugin.</summary>
    public bool Enable(string input, Action<string>? progress = null)
    {
        var packageId = PluginManifest.ResolvePackageId(input);
        var entry = _manifest.Find(packageId);
        if (entry == null)
        {
            progress?.Invoke($"{packageId} is not installed.");
            return false;
        }

        entry.Enabled = true;
        _manifest.Save();
        progress?.Invoke($"Enabled {packageId}");
        return true;
    }

    /// <summary>Disable a plugin without uninstalling.</summary>
    public bool Disable(string input, Action<string>? progress = null)
    {
        var packageId = PluginManifest.ResolvePackageId(input);
        var entry = _manifest.Find(packageId);
        if (entry == null)
        {
            progress?.Invoke($"{packageId} is not installed.");
            return false;
        }

        entry.Enabled = false;
        _manifest.Save();
        progress?.Invoke($"Disabled {packageId}");
        return true;
    }

    /// <summary>List all installed plugins.</summary>
    public IReadOnlyList<PluginEntry> List() => _manifest.Plugins;

    /// <summary>List all known shorthands.</summary>
    public static IReadOnlyDictionary<string, string> Shorthands => PluginManifest.KnownShorthands;

    /// <summary>
    /// Load all enabled plugins from the manifest and register them.
    /// Call this after <see cref="BuiltinPlugins.RegisterAllSources"/> to add runtime plugins.
    /// </summary>
    public void LoadAndRegister(SourcePluginRegistry sourceRegistry, OutputPluginRegistry outputRegistry)
    {
        foreach (var entry in _manifest.Plugins.Where(e => e.Enabled && e.InstallPath != null))
        {
            if (!Directory.Exists(entry.InstallPath))
            {
                Debug.WriteLine($"[PluginManager] Plugin dir missing: {entry.InstallPath}");
                continue;
            }

            try
            {
                var (sources, outputs) = _loader.LoadFromDirectory(entry.InstallPath!);
                foreach (var source in sources)
                    sourceRegistry.Register(source);
                foreach (var output in outputs)
                    outputRegistry.Register(output);

                Debug.WriteLine($"[PluginManager] Loaded {sources.Count} sources + {outputs.Count} outputs from {entry.PackageId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginManager] Failed to load {entry.PackageId}: {ex.Message}");
            }
        }
    }
}

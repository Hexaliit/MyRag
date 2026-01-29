using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace DoomSummarizer.Plugins.Runtime;

/// <summary>
/// Downloads NuGet packages and loads them as plugins at runtime.
/// Uses NuGet HTTP API v3 directly (no NuGet.Protocol dependency) for simplicity
/// and compatibility with single-file deployment.
/// </summary>
public sealed class PluginLoader
{
    private readonly HttpClient _httpClient;
    private readonly List<PluginLoadContext> _loadContexts = [];

    /// <summary>NuGet V3 service index URL.</summary>
    private const string NuGetServiceIndex = "https://api.nuget.org/v3/index.json";

    public PluginLoader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Download and extract a NuGet package to the plugin directory.
    /// </summary>
    /// <param name="packageId">NuGet package ID (or shorthand that has been resolved).</param>
    /// <param name="version">Specific version, or null for latest.</param>
    /// <param name="progress">Progress callback.</param>
    /// <returns>The install path, or null on failure.</returns>
    public async Task<string?> DownloadPackageAsync(
        string packageId,
        string? version = null,
        Action<string>? progress = null)
    {
        try
        {
            progress?.Invoke($"Resolving {packageId}...");

            // Resolve version if not specified
            version ??= await GetLatestVersionAsync(packageId);
            if (version == null)
            {
                progress?.Invoke($"Package {packageId} not found on NuGet.");
                return null;
            }

            progress?.Invoke($"Downloading {packageId} {version}...");

            var installDir = PluginManifest.GetPluginDir(packageId, version);
            if (Directory.Exists(installDir) && Directory.GetFiles(installDir, "*.dll").Length > 0)
            {
                progress?.Invoke($"Already installed at {installDir}");
                return installDir;
            }

            // Download the .nupkg
            var downloadUrl = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version}/{packageId.ToLowerInvariant()}.{version}.nupkg";
            var nupkgBytes = await _httpClient.GetByteArrayAsync(downloadUrl);

            progress?.Invoke($"Extracting {packageId} {version}...");

            // Extract DLLs from the nupkg (it's a ZIP)
            Directory.CreateDirectory(installDir);
            using var archive = new System.IO.Compression.ZipArchive(
                new MemoryStream(nupkgBytes), System.IO.Compression.ZipArchiveMode.Read);

            // Prefer net10.0 > net9.0 > net8.0 > netstandard2.1 > netstandard2.0
            var tfmPreference = new[] { "net10.0", "net9.0", "net8.0", "netstandard2.1", "netstandard2.0" };
            string? bestLibFolder = null;

            foreach (var tfm in tfmPreference)
            {
                var prefix = $"lib/{tfm}/";
                if (archive.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    bestLibFolder = prefix;
                    break;
                }
            }

            if (bestLibFolder == null)
            {
                progress?.Invoke($"No compatible lib folder found in {packageId}.");
                return null;
            }

            var extractedCount = 0;
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(bestLibFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var destPath = Path.Combine(installDir, entry.Name);
                entry.ExtractToFile(destPath, overwrite: true);
                extractedCount++;
            }

            progress?.Invoke($"Extracted {extractedCount} files to {installDir}");
            return installDir;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"Failed to download {packageId}: {ex.Message}");
            Debug.WriteLine($"[PluginLoader] Download error: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Load plugin assemblies from an install directory and discover plugin types.
    /// </summary>
    public (IReadOnlyList<ISourcePlugin> sources, IReadOnlyList<IOutputPlugin> outputs) LoadFromDirectory(string installDir)
    {
        var sources = new List<ISourcePlugin>();
        var outputs = new List<IOutputPlugin>();

        foreach (var dllPath in Directory.GetFiles(installDir, "*.dll"))
        {
            try
            {
                var loadContext = new PluginLoadContext(dllPath);
                _loadContexts.Add(loadContext);

                var assembly = loadContext.LoadFromAssemblyPath(dllPath);
                sources.AddRange(PluginDiscovery.DiscoverSourcePlugins(assembly));
                outputs.AddRange(PluginDiscovery.DiscoverOutputPlugins(assembly));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginLoader] Failed to load {dllPath}: {ex.Message}");
            }
        }

        return (sources, outputs);
    }

    /// <summary>Get the latest version of a NuGet package.</summary>
    private async Task<string?> GetLatestVersionAsync(string packageId)
    {
        try
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
            var json = await _httpClient.GetStringAsync(url);

            // Simple JSON parsing — extract versions array
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var versions = doc.RootElement.GetProperty("versions");
            var allVersions = new List<string>();
            foreach (var v in versions.EnumerateArray())
            {
                var ver = v.GetString();
                if (ver != null)
                    allVersions.Add(ver);
            }

            // Return latest non-prerelease, or latest overall
            return allVersions.LastOrDefault(v => !v.Contains('-'))
                   ?? allVersions.LastOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

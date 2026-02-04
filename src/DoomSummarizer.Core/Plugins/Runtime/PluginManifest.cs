using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoomSummarizer.Plugins.Runtime;

/// <summary>
///     Persisted manifest tracking installed plugins.
///     Stored at <c>~/.doomsummarizer/plugins/manifest.json</c>.
/// </summary>
public sealed class PluginManifest
{
    private static readonly string ManifestPath = Path.Combine(PluginsDirectory, "manifest.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     Well-known shorthand names that map to NuGet package IDs.
    ///     Users can type <c>plugin install source-imap</c> instead of the full package ID.
    /// </summary>
    public static readonly Dictionary<string, string> KnownShorthands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["source-hackernews"] = "Mostlylucid.DoomSummarizer.Source.HackerNews",
        ["source-hn"] = "Mostlylucid.DoomSummarizer.Source.HackerNews",
        ["source-reddit"] = "Mostlylucid.DoomSummarizer.Source.Reddit",
        ["source-google"] = "Mostlylucid.DoomSummarizer.Source.Google",
        ["source-news"] = "Mostlylucid.DoomSummarizer.Source.News",
        ["source-web"] = "Mostlylucid.DoomSummarizer.Source.Web",
        ["source-academic"] = "Mostlylucid.DoomSummarizer.Source.Academic",
        ["source-science"] = "Mostlylucid.DoomSummarizer.Source.Science",
        ["source-reference"] = "Mostlylucid.DoomSummarizer.Source.Reference",
        ["source-imap"] = "Mostlylucid.DoomSummarizer.Source.Imap",
        ["source-documents"] = "Mostlylucid.DoomSummarizer.Source.Documents",
        ["source-images"] = "Mostlylucid.DoomSummarizer.Source.Images",
        ["source-data"] = "Mostlylucid.DoomSummarizer.Source.Data",
        ["output-smtp"] = "Mostlylucid.DoomSummarizer.Output.Smtp",
        ["output-sendgrid"] = "Mostlylucid.DoomSummarizer.Output.SendGrid",

        // Processor plugins
        ["plugin-books"] = "Mostlylucid.LucidRAG.Plugins.Books",
        ["plugin-video"] = "Mostlylucid.LucidRAG.Plugins.Video",
        ["plugin-image"] = "Mostlylucid.LucidRAG.Plugins.Image",
        ["plugin-audio"] = "Mostlylucid.LucidRAG.Plugins.Audio",
        ["plugin-data"] = "Mostlylucid.LucidRAG.Plugins.Data",
        ["plugins-complete"] = "Mostlylucid.LucidRAG.Plugins.Complete"
    };

    /// <summary>Installed plugin entries.</summary>
    public List<PluginEntry> Plugins { get; set; } = [];

    /// <summary>Get the base plugins directory.</summary>
    public static string PluginsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".doomsummarizer", "plugins");

    /// <summary>
    ///     Resolve a package identifier: if it's a known shorthand, expand to the full NuGet package ID.
    ///     Otherwise, treat as an arbitrary NuGet package ID.
    /// </summary>
    public static string ResolvePackageId(string input)
    {
        return KnownShorthands.TryGetValue(input, out var packageId) ? packageId : input;
    }

    /// <summary>Load manifest from disk (or create empty).</summary>
    public static PluginManifest Load()
    {
        if (!File.Exists(ManifestPath))
            return new PluginManifest();

        var json = File.ReadAllText(ManifestPath);
        return JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions) ?? new PluginManifest();
    }

    /// <summary>Save manifest to disk.</summary>
    public void Save()
    {
        Directory.CreateDirectory(PluginsDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ManifestPath, json);
    }

    /// <summary>Find an installed plugin by package ID.</summary>
    public PluginEntry? Find(string packageId)
    {
        return Plugins.FirstOrDefault(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Get the install directory for a plugin version.</summary>
    public static string GetPluginDir(string packageId, string version)
    {
        return Path.Combine(PluginsDirectory, packageId, version);
    }
}

/// <summary>
///     A single installed plugin entry.
/// </summary>
public sealed class PluginEntry
{
    /// <summary>NuGet package ID (e.g. "Mostlylucid.DoomSummarizer.Source.Imap" or "Acme.CustomSource").</summary>
    public required string PackageId { get; set; }

    /// <summary>Installed version.</summary>
    public required string Version { get; set; }

    /// <summary>Whether this plugin is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When this plugin was installed.</summary>
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Path to the extracted DLL directory.</summary>
    public string? InstallPath { get; set; }

    /// <summary>Short name if installed via shorthand (e.g. "source-imap").</summary>
    public string? Shorthand { get; set; }
}
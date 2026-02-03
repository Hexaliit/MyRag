using System.Reflection;
using System.Text.Json.Nodes;

namespace DoomSummarizer.Services;

/// <summary>
/// Loads and manages hardware-adaptive configuration profiles.
/// Profiles are partial JSON overlays embedded as assembly resources.
/// </summary>
public static class ProfileService
{
    /// <summary>All valid profile names (excluding "dynamic" which resolves to one of these).</summary>
    public static readonly string[] ProfileNames = ["pi", "laptop", "desktop", "server", "enterprise"];

    /// <summary>Profile descriptions for --list-profiles display.</summary>
    public static readonly Dictionary<string, (string Target, string Description)> ProfileDescriptions = new()
    {
        ["pi"] = ("RPi/ARM, 1-4GB", "Minimal: tiny models, small context, reduced sources, no link-following"),
        ["laptop"] = ("8-16GB, no GPU", "Default: balanced local models with moderate context"),
        ["desktop"] = ("16-64GB, GPU", "Enhanced: larger context, GPU acceleration, batch processing"),
        ["server"] = ("32GB+, Ollama", "Ollama-primary: LLamaSharp disabled, large context, more sources"),
        ["enterprise"] = ("64GB+, full stack", "Maximum: large Ollama models, huge context, all sources, cloud fallback"),
        ["dynamic"] = ("Auto-detect", "Probes hardware and resolves to the best matching profile"),
    };

    /// <summary>
    /// Load a profile overlay as a JsonNode for deep-merging into the config.
    /// Returns null if the profile is not found.
    /// </summary>
    public static JsonNode? LoadProfile(string profileName)
    {
        var assembly = typeof(ProfileService).Assembly;
        var resourceName = $"DoomSummarizer.Resources.profiles.profile-{profileName}.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonNode.Parse(json);
    }

    /// <summary>
    /// Check if a profile name is valid (including "dynamic").
    /// </summary>
    public static bool IsValidProfile(string name)
        => name == "dynamic" || ProfileNames.Contains(name);
}

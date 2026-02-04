using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoomSummarizer.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

public static class ConfigService
{
    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".doomsummarizer");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    /// <summary>Ordered list of config sources that were loaded (for diagnostics).</summary>
    public static List<string> LoadedSources { get; } = [];

    public static string? LoadedConfigPath { get; private set; }

    /// <summary>
    ///     Optional profile name to apply during LoadAsync.
    ///     Set this before calling LoadAsync to apply a profile via CLI --profile flag.
    /// </summary>
    public static string? RequestedProfile { get; set; }

    /// <summary>
    ///     The profile name that was actually applied (after dynamic resolution).
    /// </summary>
    public static string? AppliedProfile { get; private set; }

    public static string GetConfigDir()
    {
        return ConfigDir;
    }

    /// <summary>
    ///     Load configuration with layered merging:
    ///     1. YAML embedded defaults (base)
    ///     2. Profile overlay (if --profile or config.profile is set)
    ///     3. ~/.doomsummarizer/config.json (user overrides)
    ///     4. ./doomsummarizer.json (local overrides)
    ///     Env vars and user secrets are handled separately by ApiKeyService.
    /// </summary>
    public static async Task<DoomConfig> LoadAsync()
    {
        LoadedSources.Clear();
        AppliedProfile = null;

        // 1. Load base defaults from embedded YAML (fall back to JSON)
        var baseConfig = LoadYamlDefaults();
        var baseNode = ConfigToJsonNode(baseConfig);

        // 2. Determine profile: CLI flag takes priority, then existing config "profile" field
        var profileName = RequestedProfile;
        var isCliProfile = profileName != null; // CLI --profile flag: applies LAST (overrides config files)

        // Peek at user config for a persisted profile if no CLI override
        if (profileName == null && File.Exists(ConfigPath))
            try
            {
                var peekJson = await File.ReadAllTextAsync(ConfigPath);
                var peekNode = JsonNode.Parse(peekJson);
                profileName = peekNode?["profile"]?.GetValue<string>();
            }
            catch
            {
                /* ignore parse errors in profile peek */
            }

        // 2b. Resolve profile name (needed for both early and late application)
        string? resolvedProfileName = null;
        JsonNode? profileOverlay = null;
        if (!string.IsNullOrEmpty(profileName))
        {
            resolvedProfileName = profileName;
            if (profileName == "dynamic")
                resolvedProfileName = await HardwareDetector.ResolveProfileAsync();

            profileOverlay = ProfileService.LoadProfile(resolvedProfileName);
        }

        // 2c. Apply profile BEFORE config files (persisted profile — config files can override)
        if (profileOverlay != null && !isCliProfile)
        {
            DeepMerge(baseNode, profileOverlay);
            LoadedSources.Add($"profile:{resolvedProfileName}");
            AppliedProfile = resolvedProfileName;
        }

        // 3. Merge user config (~/.doomsummarizer/config.json)
        if (File.Exists(ConfigPath))
        {
            var json = await File.ReadAllTextAsync(ConfigPath);
            var userNode = JsonNode.Parse(json);
            if (userNode != null)
            {
                DeepMerge(baseNode, userNode);
                LoadedSources.Add(ConfigPath);
            }
        }

        // 4. Merge local config (./doomsummarizer.json)
        var localConfig = Path.Combine(Directory.GetCurrentDirectory(), "doomsummarizer.json");
        if (File.Exists(localConfig))
        {
            var json = await File.ReadAllTextAsync(localConfig);
            var localNode = JsonNode.Parse(json);
            if (localNode != null)
            {
                DeepMerge(baseNode, localNode);
                LoadedSources.Add(localConfig);
            }
        }

        // 5. Apply profile AFTER config files (CLI --profile flag: overrides everything)
        if (profileOverlay != null && isCliProfile)
        {
            // Re-parse because JsonNode is consumed by the first merge
            profileOverlay = ProfileService.LoadProfile(resolvedProfileName!);
            if (profileOverlay != null)
            {
                DeepMerge(baseNode, profileOverlay);
                LoadedSources.Add($"profile:{resolvedProfileName} (override)");
                AppliedProfile = resolvedProfileName;
            }
        }

        // 6. Deserialize merged result to DoomConfig
        var mergedJson = baseNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var result = JsonSerializer.Deserialize(mergedJson, DoomConfigContext.Default.DoomConfig);

        LoadedConfigPath = LoadedSources.Count > 0 ? LoadedSources[^1] : "embedded defaults";
        return result ?? new DoomConfig();
    }

    /// <summary>
    ///     Load defaults from embedded YAML resource. Falls back to embedded JSON if YAML is missing.
    /// </summary>
    internal static DoomConfig LoadYamlDefaults()
    {
        var assembly = typeof(ConfigService).Assembly;
        var yamlResourceName = "DoomSummarizer.Resources.default-config.yaml";

        using var stream = assembly.GetManifestResourceStream(yamlResourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var config = deserializer.Deserialize<DoomConfig>(yaml);
            LoadedSources.Add("embedded default-config.yaml");
            return config ?? new DoomConfig();
        }

        // Fall back to embedded JSON
        return LoadJsonDefaults();
    }

    /// <summary>
    ///     Load the raw YAML text from the embedded resource (for --reference output).
    /// </summary>
    public static string? LoadYamlDefaultsText()
    {
        var assembly = typeof(ConfigService).Assembly;
        var yamlResourceName = "DoomSummarizer.Resources.default-config.yaml";

        using var stream = assembly.GetManifestResourceStream(yamlResourceName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static DoomConfig LoadJsonDefaults()
    {
        var assembly = typeof(ConfigService).Assembly;
        var resourceName = "DoomSummarizer.Resources.default-config.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Debug.WriteLine("Warning: Could not load default config, using hardcoded defaults");
            LoadedSources.Add("hardcoded defaults");
            return new DoomConfig();
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        LoadedSources.Add("embedded default-config.json");
        return JsonSerializer.Deserialize(json, DoomConfigContext.Default.DoomConfig) ?? new DoomConfig();
    }

    /// <summary>
    ///     Convert a DoomConfig to a JsonNode tree for merging.
    ///     Uses the same camelCase serializer options as DoomConfigContext.
    /// </summary>
    internal static JsonNode ConfigToJsonNode(DoomConfig config)
    {
        var json = JsonSerializer.Serialize(config, DoomConfigContext.Default.DoomConfig);
        return JsonNode.Parse(json)!;
    }

    /// <summary>
    ///     Deep-merge <paramref name="overrides" /> into <paramref name="baseNode" /> in place.
    ///     Objects are merged key-by-key; scalars and arrays are replaced outright.
    /// </summary>
    internal static void DeepMerge(JsonNode baseNode, JsonNode overrides)
    {
        if (baseNode is not JsonObject baseObj || overrides is not JsonObject overrideObj)
            return;

        foreach (var (key, value) in overrideObj)
        {
            if (value == null)
            {
                // Explicit null in override removes the key
                baseObj.Remove(key);
                continue;
            }

            if (baseObj.ContainsKey(key) && baseObj[key] is JsonObject existingObj &&
                value is JsonObject nestedOverride)
            {
                // Recursively merge nested objects
                DeepMerge(existingObj, nestedOverride);
            }
            else
            {
                // Replace scalar, array, or add new key
                baseObj.Remove(key);
                baseObj.Add(key, value.DeepClone());
            }
        }
    }

    /// <summary>
    ///     Compare two configs and return keys where the effective value differs from defaults.
    ///     Returns a flat dictionary of dotted paths to their effective values.
    /// </summary>
    public static Dictionary<string, string> GetOverrides(DoomConfig defaults, DoomConfig effective)
    {
        var defaultNode = ConfigToJsonNode(defaults);
        var effectiveNode = ConfigToJsonNode(effective);
        var overrides = new Dictionary<string, string>();
        CollectDiffs("", defaultNode, effectiveNode, overrides);
        return overrides;
    }

    private static void CollectDiffs(string prefix, JsonNode? defaultNode, JsonNode? effectiveNode,
        Dictionary<string, string> result)
    {
        if (defaultNode is JsonObject defaultObj && effectiveNode is JsonObject effectiveObj)
        {
            foreach (var (key, _) in effectiveObj)
            {
                var path = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
                CollectDiffs(path, defaultObj[key], effectiveObj[key], result);
            }
        }
        else
        {
            var dStr = defaultNode?.ToJsonString() ?? "null";
            var eStr = effectiveNode?.ToJsonString() ?? "null";
            if (dStr != eStr)
                result[prefix] = eStr;
        }
    }

    public static async Task SaveAsync(DoomConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, DoomConfigContext.Default.DoomConfig);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    /// <summary>
    ///     Generate a starter config.json with commonly-changed settings only.
    /// </summary>
    public static async Task InitializeStarterConfigAsync()
    {
        Directory.CreateDirectory(ConfigDir);
        var starter = new JsonObject
        {
            ["ollama"] = new JsonObject
            {
                ["model"] = "gemma3:4b",
                ["baseUrl"] = "http://localhost:11434"
            },
            ["storage"] = new JsonObject
            {
                ["dbPath"] = "~/.doomsummarizer/doom.db"
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = starter.ToJsonString(options);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    /// <summary>
    ///     Generate a full config.json with all settings (for --init --full).
    /// </summary>
    public static async Task InitializeFullConfigAsync()
    {
        Directory.CreateDirectory(ConfigDir);
        var config = LoadYamlDefaults();
        await SaveAsync(config);
    }

    [Obsolete("Use InitializeStarterConfigAsync or InitializeFullConfigAsync instead")]
    public static async Task InitializeDefaultConfigAsync()
    {
        await InitializeFullConfigAsync();
    }

    public static string GetDbPath(DoomConfig config)
    {
        var path = config.Storage.DbPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return path;
    }

    public static string GetVectorDbPath()
    {
        var path = Path.Combine(ConfigDir, "vectors.duckdb");
        Directory.CreateDirectory(ConfigDir);
        return path;
    }
}
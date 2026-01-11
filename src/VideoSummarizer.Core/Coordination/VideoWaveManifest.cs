using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VideoSummarizer.Core.Coordination;

/// <summary>
/// Wave manifest loaded from YAML configuration.
/// All configuration values are defined here - NO magic numbers in code.
/// </summary>
public record VideoWaveManifest
{
    public required string Name { get; init; }
    public int Priority { get; init; } = 500;
    public bool Enabled { get; init; } = true;
    public List<string> Tags { get; init; } = [];
    public string Lane { get; init; } = "default";

    public ListensConfig Listens { get; init; } = new();
    public EmitsConfig Emits { get; init; } = new();
    public CacheConfig Cache { get; init; } = new();
    public Dictionary<string, object> Config { get; init; } = new();
    public EscalationConfig? Escalation { get; init; }
}

public record ListensConfig
{
    public List<string> Required { get; init; } = [];
    public List<string> Optional { get; init; } = [];
}

public record EmitsConfig
{
    [YamlMember(Alias = "on_start")]
    public List<string> OnStart { get; init; } = [];

    [YamlMember(Alias = "on_complete")]
    public List<SignalEmit> OnComplete { get; init; } = [];

    [YamlMember(Alias = "on_failure")]
    public List<string> OnFailure { get; init; } = [];
}

public record SignalEmit
{
    public required string Key { get; init; }
    public string Type { get; init; } = "object";
}

public record CacheConfig
{
    public List<CacheEmit> Emits { get; init; } = [];
    public List<string> Uses { get; init; } = [];
}

public record CacheEmit
{
    public required string Key { get; init; }
    public string Type { get; init; } = "object";
}

public record EscalationConfig
{
    public Dictionary<string, EscalationTarget> Targets { get; init; } = new();
}

public record EscalationTarget
{
    public List<EscalationCondition> When { get; init; } = [];

    [YamlMember(Alias = "skip_when")]
    public List<EscalationCondition> SkipWhen { get; init; } = [];
}

public record LaneConfig
{
    [YamlMember(Alias = "max_concurrency")]
    public int MaxConcurrency { get; init; } = 1;
    public string Description { get; init; } = "";
}

public record WavesConfig
{
    public DefaultsConfig Defaults { get; init; } = new();
    public Dictionary<string, LaneConfig> Lanes { get; init; } = new();

    // Individual wave manifests (dynamic)
    [YamlIgnore]
    public Dictionary<string, VideoWaveManifest> Waves { get; set; } = new();
}

public record DefaultsConfig
{
    public string Lane { get; init; } = "default";
    public int Concurrency { get; init; } = 1;

    [YamlMember(Alias = "timeout_seconds")]
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// Loads and caches wave manifests from YAML configuration.
/// </summary>
public class VideoWaveManifestLoader
{
    private readonly Dictionary<string, VideoWaveManifest> _manifests = new();
    private readonly Dictionary<string, LaneConfig> _lanes = new();
    private readonly DefaultsConfig _defaults = new();
    private bool _loaded;

    public VideoWaveManifestLoader()
    {
    }

    /// <summary>
    /// Load manifests from YAML file.
    /// </summary>
    public void LoadFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        LoadFromYaml(yaml);
    }

    /// <summary>
    /// Load manifests from embedded resource.
    /// </summary>
    public void LoadFromEmbedded()
    {
        var assembly = typeof(VideoWaveManifestLoader).Assembly;
        var resourceName = "VideoSummarizer.Core.Config.waves.yaml";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            // Try to load from file relative to assembly
            var assemblyPath = Path.GetDirectoryName(assembly.Location)!;
            var filePath = Path.Combine(assemblyPath, "Config", "waves.yaml");

            if (File.Exists(filePath))
            {
                LoadFromFile(filePath);
                return;
            }

            throw new FileNotFoundException($"Could not find waves.yaml manifest");
        }

        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        LoadFromYaml(yaml);
    }

    /// <summary>
    /// Load manifests from YAML string.
    /// </summary>
    public void LoadFromYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Parse as dictionary first to handle mixed structure
        var raw = deserializer.Deserialize<Dictionary<string, object>>(yaml);

        // Extract defaults
        if (raw.TryGetValue("defaults", out var defaultsObj))
        {
            // Re-serialize and deserialize as proper type
            var serializer = new SerializerBuilder().Build();
            var defaultsYaml = serializer.Serialize(defaultsObj);
            var defaults = deserializer.Deserialize<DefaultsConfig>(defaultsYaml);
            // Store defaults
        }

        // Extract lanes
        if (raw.TryGetValue("lanes", out var lanesObj))
        {
            var serializer = new SerializerBuilder().Build();
            var lanesYaml = serializer.Serialize(lanesObj);
            var lanes = deserializer.Deserialize<Dictionary<string, LaneConfig>>(lanesYaml);
            foreach (var (name, config) in lanes)
            {
                _lanes[name] = config;
            }
        }

        // Extract individual wave manifests (everything else)
        var excludeKeys = new HashSet<string> { "defaults", "lanes" };
        foreach (var (key, value) in raw)
        {
            if (excludeKeys.Contains(key)) continue;

            var serializer = new SerializerBuilder().Build();
            var waveYaml = serializer.Serialize(value);

            try
            {
                var manifest = deserializer.Deserialize<VideoWaveManifest>(waveYaml);
                if (manifest != null)
                {
                    _manifests[key] = manifest;
                }
            }
            catch
            {
                // Skip entries that don't match wave manifest structure
            }
        }

        _loaded = true;
    }

    /// <summary>
    /// Get manifest for a specific wave.
    /// </summary>
    public VideoWaveManifest? GetManifest(string waveName)
    {
        EnsureLoaded();
        return _manifests.GetValueOrDefault(waveName);
    }

    /// <summary>
    /// Get all manifests ordered by priority (descending).
    /// </summary>
    public IReadOnlyList<VideoWaveManifest> GetOrderedManifests()
    {
        EnsureLoaded();
        return _manifests.Values
            .OrderByDescending(m => m.Priority)
            .ToList();
    }

    /// <summary>
    /// Get manifests that can run given available signals.
    /// </summary>
    public IReadOnlyList<VideoWaveManifest> GetRunnableManifests(IReadOnlySet<string> availableSignals)
    {
        EnsureLoaded();
        return _manifests.Values
            .Where(m => m.Enabled)
            .Where(m => CanRun(m, availableSignals))
            .OrderByDescending(m => m.Priority)
            .ToList();
    }

    /// <summary>
    /// Check if a wave can run based on available signals.
    /// </summary>
    public bool CanRun(VideoWaveManifest manifest, IReadOnlySet<string> availableSignals)
    {
        // All required signals must be available
        foreach (var required in manifest.Listens.Required)
        {
            if (!availableSignals.Contains(required))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Get lane configuration.
    /// </summary>
    public LaneConfig GetLane(string laneName)
    {
        EnsureLoaded();
        return _lanes.GetValueOrDefault(laneName, new LaneConfig { MaxConcurrency = 1 });
    }

    /// <summary>
    /// Build dependency graph for visualization.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        EnsureLoaded();
        var graph = new Dictionary<string, HashSet<string>>();

        foreach (var manifest in _manifests.Values)
        {
            graph[manifest.Name] = manifest.Listens.Required.ToHashSet();
        }

        return graph;
    }

    /// <summary>
    /// Get config value from manifest.
    /// </summary>
    public T? GetConfigValue<T>(string waveName, string key, T? defaultValue = default)
    {
        var manifest = GetManifest(waveName);
        if (manifest?.Config.TryGetValue(key, out var value) == true)
        {
            if (value is T typed)
                return typed;

            // Try conversion
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            try
            {
                LoadFromEmbedded();
            }
            catch
            {
                // Use empty defaults
                _loaded = true;
            }
        }
    }
}

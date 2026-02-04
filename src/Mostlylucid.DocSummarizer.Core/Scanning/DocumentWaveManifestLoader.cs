using System.Reflection;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.DocSummarizer.Scanning;

/// <summary>
///     Loads document wave manifests from YAML files for dynamic composition.
///     Supports both embedded resources and external files.
/// </summary>
public sealed class DocumentWaveManifestLoader
{
    private readonly IDeserializer _deserializer;
    private readonly Dictionary<string, DocumentWaveManifest> _manifests = new();
    private readonly Dictionary<string, PipelineDefinition> _pipelines = new();

    public DocumentWaveManifestLoader()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    ///     Load all wave manifests from embedded resources.
    /// </summary>
    public IReadOnlyDictionary<string, DocumentWaveManifest> LoadEmbeddedManifests()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".wave.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();
            var manifest = _deserializer.Deserialize<DocumentWaveManifest>(yaml);

            if (manifest != null && !string.IsNullOrEmpty(manifest.Name)) _manifests[manifest.Name] = manifest;
        }

        return _manifests;
    }

    /// <summary>
    ///     Load wave manifests from a directory.
    /// </summary>
    public IReadOnlyDictionary<string, DocumentWaveManifest> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return _manifests;

        var files = Directory.GetFiles(directory, "*.wave.yaml", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            var yaml = File.ReadAllText(file);
            var manifest = _deserializer.Deserialize<DocumentWaveManifest>(yaml);

            if (manifest != null && !string.IsNullOrEmpty(manifest.Name)) _manifests[manifest.Name] = manifest;
        }

        return _manifests;
    }

    /// <summary>
    ///     Load all pipeline definitions from embedded resources.
    /// </summary>
    public IReadOnlyDictionary<string, PipelineDefinition> LoadEmbeddedPipelines()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Pipelines.") && n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                                  && !n.EndsWith(".wave.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();
            var pipeline = _deserializer.Deserialize<PipelineDefinition>(yaml);

            if (pipeline != null && !string.IsNullOrEmpty(pipeline.Name)) _pipelines[pipeline.Name] = pipeline;
        }

        return _pipelines;
    }

    /// <summary>
    ///     Load pipeline definitions from a directory.
    /// </summary>
    public IReadOnlyDictionary<string, PipelineDefinition> LoadPipelinesFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return _pipelines;

        var files = Directory.GetFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".wave.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var yaml = File.ReadAllText(file);
            var pipeline = _deserializer.Deserialize<PipelineDefinition>(yaml);

            if (pipeline != null && !string.IsNullOrEmpty(pipeline.Name)) _pipelines[pipeline.Name] = pipeline;
        }

        return _pipelines;
    }

    /// <summary>
    ///     Get a specific manifest by wave name.
    /// </summary>
    public DocumentWaveManifest? GetManifest(string waveName)
    {
        return _manifests.TryGetValue(waveName, out var manifest) ? manifest : null;
    }

    /// <summary>
    ///     Get a specific pipeline by name.
    /// </summary>
    public PipelineDefinition? GetPipeline(string pipelineName)
    {
        return _pipelines.TryGetValue(pipelineName, out var pipeline) ? pipeline : null;
    }

    /// <summary>
    ///     Get all loaded manifests.
    /// </summary>
    public IReadOnlyDictionary<string, DocumentWaveManifest> GetAllManifests()
    {
        return _manifests;
    }

    /// <summary>
    ///     Get all loaded pipelines.
    /// </summary>
    public IReadOnlyDictionary<string, PipelineDefinition> GetAllPipelines()
    {
        return _pipelines;
    }

    /// <summary>
    ///     Get all manifests sorted by priority (highest first).
    /// </summary>
    public IReadOnlyList<DocumentWaveManifest> GetOrderedManifests()
    {
        return _manifests.Values
            .OrderByDescending(m => m.Taxonomy.Priority)
            .ToList();
    }

    /// <summary>
    ///     Get wave names for a specific pipeline.
    /// </summary>
    public IReadOnlyList<string> GetPipelineWaves(string pipelineName)
    {
        if (!_pipelines.TryGetValue(pipelineName, out var pipeline))
            return [];

        var waves = new List<string>();
        waves.AddRange(pipeline.Waves.Required);
        waves.AddRange(pipeline.Waves.Conditional);
        return waves;
    }

    /// <summary>
    ///     Get all signal contracts for documentation.
    /// </summary>
    public string GetSignalContractsSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Document Wave Signal Contracts");
        sb.AppendLine();

        foreach (var manifest in GetOrderedManifests())
        {
            sb.AppendLine($"## {manifest.Name}");
            sb.AppendLine($"Priority: {manifest.Taxonomy.Priority}");
            sb.AppendLine($"Kind: {manifest.Taxonomy.Kind}");
            sb.AppendLine();

            if (manifest.Emits.Count > 0)
            {
                sb.AppendLine("**Emits:**");
                foreach (var signal in manifest.Emits)
                    sb.AppendLine($"  - `{signal.Key}` ({signal.Type}): {signal.Description}");
                sb.AppendLine();
            }

            if (manifest.Signals.Count > 0)
            {
                sb.AppendLine("**Signals:**");
                foreach (var signal in manifest.Signals) sb.AppendLine($"  - `{signal}`");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
///     Pipeline definition loaded from YAML.
/// </summary>
public class PipelineDefinition
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";

    [YamlMember(Alias = "description")] public string Description { get; set; } = "";

    [YamlMember(Alias = "version")] public int Version { get; set; } = 1;

    [YamlMember(Alias = "signals")] public List<string> Signals { get; set; } = [];

    [YamlMember(Alias = "waves")] public PipelineWaves Waves { get; set; } = new();

    [YamlMember(Alias = "output")] public PipelineOutput Output { get; set; } = new();

    [YamlMember(Alias = "escalation")] public PipelineEscalation Escalation { get; set; } = new();

    [YamlMember(Alias = "models")] public PipelineModels Models { get; set; } = new();

    [YamlMember(Alias = "budget")] public PipelineBudget Budget { get; set; } = new();
}

public class PipelineWaves
{
    [YamlMember(Alias = "required")] public List<string> Required { get; set; } = [];

    [YamlMember(Alias = "conditional")] public List<string> Conditional { get; set; } = [];
}

public class PipelineOutput
{
    [YamlMember(Alias = "format")] public string Format { get; set; } = "markdown";

    [YamlMember(Alias = "include_metadata")]
    public bool IncludeMetadata { get; set; } = true;

    [YamlMember(Alias = "include_confidence")]
    public bool IncludeConfidence { get; set; } = true;

    [YamlMember(Alias = "show_extraction_method")]
    public bool ShowExtractionMethod { get; set; } = true;
}

public class PipelineEscalation
{
    [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "triggers")] public List<EscalationTrigger> Triggers { get; set; } = [];
}

public class EscalationTrigger
{
    [YamlMember(Alias = "signal")] public string Signal { get; set; } = "";

    [YamlMember(Alias = "condition")] public string? Condition { get; set; }

    [YamlMember(Alias = "value")] public object? Value { get; set; }

    [YamlMember(Alias = "target")] public string Target { get; set; } = "";

    [YamlMember(Alias = "reason")] public string? Reason { get; set; }
}

public class PipelineModels
{
    [YamlMember(Alias = "default")] public string Default { get; set; } = "minicpm-v:8b";

    [YamlMember(Alias = "provider")] public string Provider { get; set; } = "ollama";

    [YamlMember(Alias = "tiers")] public Dictionary<string, ModelTierConfig> Tiers { get; set; } = new();
}

public class ModelTierConfig
{
    [YamlMember(Alias = "model")] public string Model { get; set; } = "";

    [YamlMember(Alias = "provider")] public string Provider { get; set; } = "ollama";

    [YamlMember(Alias = "for")] public string? For { get; set; }
}

public class PipelineBudget
{
    [YamlMember(Alias = "timeout_ms")] public int TimeoutMs { get; set; } = 300000;

    [YamlMember(Alias = "max_pages")] public int MaxPages { get; set; } = 100;

    [YamlMember(Alias = "max_cost_usd")] public double MaxCostUsd { get; set; } = 1.0;
}
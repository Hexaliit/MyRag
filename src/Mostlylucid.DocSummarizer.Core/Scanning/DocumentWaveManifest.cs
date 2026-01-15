using YamlDotNet.Serialization;

namespace Mostlylucid.DocSummarizer.Scanning;

/// <summary>
/// YAML manifest for document scanning waves.
/// Defines wave behavior, signals, and escalation rules declaratively.
/// </summary>
public class DocumentWaveManifest
{
    /// <summary>
    /// Wave identifier (e.g., "native-extraction", "vlm-ocr").
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Human-readable description.
    /// </summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Manifest version.
    /// </summary>
    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Wave taxonomy - kind, determinism, etc.
    /// </summary>
    [YamlMember(Alias = "taxonomy")]
    public WaveTaxonomy Taxonomy { get; set; } = new();

    /// <summary>
    /// Signals this wave consumes.
    /// </summary>
    [YamlMember(Alias = "signals")]
    public List<string> Signals { get; set; } = [];

    /// <summary>
    /// Signal contracts this wave emits.
    /// </summary>
    [YamlMember(Alias = "emits")]
    public List<SignalContract> Emits { get; set; } = [];

    /// <summary>
    /// Trigger conditions for this wave.
    /// </summary>
    [YamlMember(Alias = "triggers")]
    public WaveTriggers Triggers { get; set; } = new();

    /// <summary>
    /// Model/sensor configuration.
    /// </summary>
    [YamlMember(Alias = "model")]
    public ModelConfig Model { get; set; } = new();

    /// <summary>
    /// Escalation rules.
    /// </summary>
    [YamlMember(Alias = "escalation")]
    public EscalationConfig Escalation { get; set; } = new();

    /// <summary>
    /// Budget constraints.
    /// </summary>
    [YamlMember(Alias = "budget")]
    public BudgetConfig Budget { get; set; } = new();

    /// <summary>
    /// Caching configuration.
    /// </summary>
    [YamlMember(Alias = "cache")]
    public CacheConfig Cache { get; set; } = new();

    /// <summary>
    /// Default values.
    /// </summary>
    [YamlMember(Alias = "defaults")]
    public WaveDefaults Defaults { get; set; } = new();
}

/// <summary>
/// Wave taxonomy classification.
/// </summary>
public class WaveTaxonomy
{
    /// <summary>
    /// Wave kind: sensor, extractor, transformer, proposer.
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string Kind { get; set; } = "extractor";

    /// <summary>
    /// Whether output is deterministic (same input = same output).
    /// </summary>
    [YamlMember(Alias = "deterministic")]
    public bool Deterministic { get; set; } = true;

    /// <summary>
    /// Processing lane for concurrency control.
    /// </summary>
    [YamlMember(Alias = "lane")]
    public string Lane { get; set; } = "default";

    /// <summary>
    /// Execution priority (higher = runs first).
    /// </summary>
    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 50;

    /// <summary>
    /// Tags for filtering.
    /// </summary>
    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; set; } = [];
}

/// <summary>
/// Signal contract definition.
/// </summary>
public class SignalContract
{
    /// <summary>
    /// Signal key pattern (e.g., "content.text", "table.*").
    /// </summary>
    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// Signal type: string, number, boolean, array, object.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "string";

    /// <summary>
    /// Description of this signal.
    /// </summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Whether this signal is optional.
    /// </summary>
    [YamlMember(Alias = "optional")]
    public bool Optional { get; set; } = false;
}

/// <summary>
/// Wave trigger conditions.
/// </summary>
public class WaveTriggers
{
    /// <summary>
    /// Required signals for this wave to run.
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<string> Requires { get; set; } = [];

    /// <summary>
    /// Signals that cause this wave to skip.
    /// </summary>
    [YamlMember(Alias = "skip_if")]
    public List<string> SkipIf { get; set; } = [];

    /// <summary>
    /// File extensions this wave handles.
    /// </summary>
    [YamlMember(Alias = "extensions")]
    public List<string> Extensions { get; set; } = [];

    /// <summary>
    /// Minimum confidence threshold to trigger.
    /// </summary>
    [YamlMember(Alias = "min_confidence")]
    public double MinConfidence { get; set; } = 0.0;
}

/// <summary>
/// Model/sensor configuration.
/// </summary>
public class ModelConfig
{
    /// <summary>
    /// Model provider (e.g., "ollama", "anthropic", "openai", "nanonets").
    /// </summary>
    [YamlMember(Alias = "provider")]
    public string Provider { get; set; } = "ollama";

    /// <summary>
    /// Model name (e.g., "minicpm-v:8b", "olmocr-2", "claude-3-5-sonnet").
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Base URL for the model API.
    /// </summary>
    [YamlMember(Alias = "base_url")]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Temperature for generation.
    /// </summary>
    [YamlMember(Alias = "temperature")]
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Maximum tokens to generate.
    /// </summary>
    [YamlMember(Alias = "max_tokens")]
    public int MaxTokens { get; set; } = 8192;

    /// <summary>
    /// Custom system prompt.
    /// </summary>
    [YamlMember(Alias = "system_prompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Fallback models if primary fails.
    /// </summary>
    [YamlMember(Alias = "fallback")]
    public List<string> Fallback { get; set; } = [];
}

/// <summary>
/// Escalation configuration.
/// </summary>
public class EscalationConfig
{
    /// <summary>
    /// Enable automatic escalation.
    /// </summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Escalate when text density is below this threshold.
    /// </summary>
    [YamlMember(Alias = "on_low_text_density")]
    public int OnLowTextDensity { get; set; } = 100;

    /// <summary>
    /// Escalate when OCR confidence is below this threshold.
    /// </summary>
    [YamlMember(Alias = "on_low_ocr_confidence")]
    public double OnLowOcrConfidence { get; set; } = 0.7;

    /// <summary>
    /// Escalate for complex tables.
    /// </summary>
    [YamlMember(Alias = "on_complex_tables")]
    public bool OnComplexTables { get; set; } = true;

    /// <summary>
    /// Escalate for charts/figures.
    /// </summary>
    [YamlMember(Alias = "on_charts")]
    public bool OnCharts { get; set; } = true;

    /// <summary>
    /// Skip escalation if native extraction confidence is high.
    /// </summary>
    [YamlMember(Alias = "skip_if_high_confidence")]
    public double SkipIfHighConfidence { get; set; } = 0.85;

    /// <summary>
    /// Target wave to escalate to.
    /// </summary>
    [YamlMember(Alias = "target")]
    public string? Target { get; set; }
}

/// <summary>
/// Budget constraints.
/// </summary>
public class BudgetConfig
{
    /// <summary>
    /// Maximum time in milliseconds.
    /// </summary>
    [YamlMember(Alias = "timeout_ms")]
    public int TimeoutMs { get; set; } = 60000;

    /// <summary>
    /// Maximum tokens to use.
    /// </summary>
    [YamlMember(Alias = "max_tokens")]
    public int MaxTokens { get; set; } = 100000;

    /// <summary>
    /// Maximum cost in USD (for cloud APIs).
    /// </summary>
    [YamlMember(Alias = "max_cost_usd")]
    public double MaxCostUsd { get; set; } = 1.0;

    /// <summary>
    /// Maximum pages to process per document.
    /// </summary>
    [YamlMember(Alias = "max_pages")]
    public int MaxPages { get; set; } = 100;
}

/// <summary>
/// Caching configuration.
/// </summary>
public class CacheConfig
{
    /// <summary>
    /// Enable caching for this wave.
    /// </summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cache time-to-live in seconds.
    /// </summary>
    [YamlMember(Alias = "ttl_seconds")]
    public int TtlSeconds { get; set; } = 86400; // 24 hours

    /// <summary>
    /// Cache key strategy: content_hash, path, both.
    /// </summary>
    [YamlMember(Alias = "key_strategy")]
    public string KeyStrategy { get; set; } = "content_hash";
}

/// <summary>
/// Default values for wave configuration.
/// </summary>
public class WaveDefaults
{
    /// <summary>
    /// Weight multipliers for scoring.
    /// </summary>
    [YamlMember(Alias = "weights")]
    public WeightDefaults Weights { get; set; } = new();

    /// <summary>
    /// Confidence thresholds.
    /// </summary>
    [YamlMember(Alias = "confidence")]
    public ConfidenceDefaults Confidence { get; set; } = new();

    /// <summary>
    /// Feature flags.
    /// </summary>
    [YamlMember(Alias = "features")]
    public FeatureDefaults Features { get; set; } = new();

    /// <summary>
    /// Custom parameters.
    /// </summary>
    [YamlMember(Alias = "parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public class WeightDefaults
{
    [YamlMember(Alias = "base")]
    public double Base { get; set; } = 1.0;

    [YamlMember(Alias = "high_confidence")]
    public double HighConfidence { get; set; } = 1.5;

    [YamlMember(Alias = "low_confidence")]
    public double LowConfidence { get; set; } = 0.5;
}

public class ConfidenceDefaults
{
    [YamlMember(Alias = "high")]
    public double High { get; set; } = 0.9;

    [YamlMember(Alias = "medium")]
    public double Medium { get; set; } = 0.7;

    [YamlMember(Alias = "low")]
    public double Low { get; set; } = 0.5;

    [YamlMember(Alias = "threshold")]
    public double Threshold { get; set; } = 0.5;
}

public class FeatureDefaults
{
    [YamlMember(Alias = "detailed_logging")]
    public bool DetailedLogging { get; set; } = false;

    [YamlMember(Alias = "enable_cache")]
    public bool EnableCache { get; set; } = true;

    [YamlMember(Alias = "can_escalate")]
    public bool CanEscalate { get; set; } = true;

    [YamlMember(Alias = "parallel_pages")]
    public bool ParallelPages { get; set; } = false;
}

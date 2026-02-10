using YamlDotNet.Serialization;

namespace DoomSummarizer.Models;

/// <summary>
///     A single query exemplar: a representative question with classification metadata.
///     Loaded from YAML files (embedded defaults + user overrides in ~/.doomsummarizer/exemplars/).
///     Pre-embedded at startup for deterministic cosine-similarity classification.
/// </summary>
public class QueryExemplar
{
    [YamlMember(Alias = "question")] public string Question { get; set; } = "";

    [YamlMember(Alias = "topic")] public string Topic { get; set; } = "default";

    [YamlMember(Alias = "type")] public string Type { get; set; } = "roundup";

    /// <summary>
    ///     Optional source hints — preferred sources for this exemplar's topic+type combo.
    ///     When present, these are factored into source selection for matching queries.
    /// </summary>
    [YamlMember(Alias = "sources")]
    public List<string>? Sources { get; set; }

    /// <summary>
    ///     Optional vibe/tone hint — detected tone from query phrasing.
    ///     Values: doom, hopeful, snarky, funny, upbeat, friendly, toon, neutral.
    ///     Enriches keyword-based vibe detection with semantic similarity.
    /// </summary>
    [YamlMember(Alias = "vibe")]
    public string? Vibe { get; set; }

    /// <summary>
    ///     Optional complexity hint — whether the query needs nuanced/multi-step analysis.
    ///     Values: simple (default), complex (sentinel may help with decomposition).
    /// </summary>
    [YamlMember(Alias = "complexity")]
    public string? Complexity { get; set; }
}

/// <summary>
///     Root document for exemplar YAML files.
/// </summary>
public class ExemplarDocument
{
    [YamlMember(Alias = "exemplars")] public List<QueryExemplar> Exemplars { get; set; } = [];
}

/// <summary>
///     Result of embedding-based query classification.
///     Contains deterministic category scores and query type detection.
/// </summary>
public record QueryClassification
{
    /// <summary>Category (topic) → weighted vote score across matching exemplars.</summary>
    public Dictionary<string, double> Categories { get; init; } = new();

    /// <summary>Detected query type from exemplar matching (roundup, qa, howto, deep_dive, comparison, composite, search_only, trend, news).</summary>
    public string QueryType { get; init; } = "roundup";

    /// <summary>Confidence of the query type detection (weighted vote score).</summary>
    public double QueryTypeConfidence { get; init; }

    /// <summary>Detected vibe/tone from exemplar matching (doom, hopeful, snarky, etc.).</summary>
    public string? Vibe { get; init; }

    /// <summary>Confidence of the vibe detection (weighted vote score).</summary>
    public double VibeConfidence { get; init; }

    /// <summary>Whether the query matches composite exemplars (multi-part, needs decomposition).</summary>
    public bool IsComposite { get; init; }

    /// <summary>Whether the query matches complex exemplars (needs nuanced sentinel analysis).</summary>
    public bool IsComplex { get; init; }

    /// <summary>Source hints from the best-matching exemplar, if any.</summary>
    public List<string>? SourceHints { get; init; }

    /// <summary>The best-matching exemplar question (for debug output).</summary>
    public string? BestMatch { get; init; }

    /// <summary>Similarity score of the best overall match.</summary>
    public double BestMatchScore { get; init; }

    /// <summary>Top matching exemplars for debug output.</summary>
    public List<ScoredExemplar> TopMatches { get; init; } = [];

    /// <summary>Structural features extracted from the query (non-null for short queries).</summary>
    public object? Features { get; init; }
}

/// <summary>
///     A scored exemplar match — for debug/diagnostic output.
/// </summary>
public record ScoredExemplar(string Question, string Topic, string Type, double Score);

using Mostlylucid.DocSummarizer.Services.Onnx;

namespace LucidRAG.Decomposer.Models;

/// <summary>
/// The fundamental execution unit in the decomposition tree.
/// Each node represents a single query (atomic or composite) with
/// pre-computed metadata for execution routing.
/// </summary>
public record QueryNode
{
    /// <summary>Unique node ID (GUID).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The sub-query text.</summary>
    public required string Query { get; init; }

    /// <summary>Node type driving execution strategy.</summary>
    public QueryNodeType Type { get; init; } = QueryNodeType.Atomic;

    /// <summary>Pre-computed embedding for cache/similarity checks.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Child nodes (for composite queries).</summary>
    public List<QueryNode> Children { get; init; } = [];

    /// <summary>Parent node ID (back-reference for tree traversal).</summary>
    public string? ParentId { get; init; }

    /// <summary>Recursion depth (max 3).</summary>
    public int Depth { get; init; }

    // --- Content reference ---

    /// <summary>URL/file/DOI to fetch directly (for ContentReference nodes).</summary>
    public ContentReference? Reference { get; init; }

    // --- Execution metadata ---

    /// <summary>Time constraints for this sub-query.</summary>
    public TemporalScope? TimeScope { get; init; }

    /// <summary>Filter keywords from sentinel (for Lucene pre-filtering).</summary>
    public List<string> FilterKeywords { get; init; } = [];

    /// <summary>Optimized search terms for web/news APIs.</summary>
    public List<string> SearchQueries { get; init; } = [];

    /// <summary>NER entities scoped to this sub-query.</summary>
    public List<NerEntity> RelevantEntities { get; init; } = [];

    // --- Tool routing ---

    /// <summary>
    /// Tool action this node requires for execution. When set, the orchestrator
    /// routes to the tool executor instead of the default search pipeline.
    /// "Sources are tools" — search, file system, indexer, KB query, crawl.
    /// </summary>
    public ToolAction? ToolAction { get; init; }

    // --- Execution ordering ---

    /// <summary>KnowledgeGap nodes run before main query nodes.</summary>
    public bool IsPrerequisite { get; init; }

    /// <summary>Whether results should be persisted to KB (KnowledgeGap nodes).</summary>
    public bool StoreResultInKb { get; init; }

    // --- Concept metadata ---

    /// <summary>Detected concept type driving retrieval strategy + response shape.</summary>
    public ConceptType? Concept { get; init; }

    // --- Execution state (mutable for orchestrator) ---

    /// <summary>Current execution status.</summary>
    public QueryNodeStatus Status { get; set; } = QueryNodeStatus.Pending;

    /// <summary>Cache hit result (if resolved from cache).</summary>
    public CacheHit? CacheResult { get; set; }

    /// <summary>KB probe result (if resolved from knowledge base).</summary>
    public KbProbeResult? KbResult { get; set; }
}

public enum QueryNodeType
{
    /// <summary>Single atomic query — fetched directly.</summary>
    Atomic,

    /// <summary>Contains children that must all execute.</summary>
    Composite,

    /// <summary>URL/file/DOI reference — fetch directly, not search.</summary>
    ContentReference,

    /// <summary>Comparison node — two sides executed and interleaved.</summary>
    Comparison,

    /// <summary>Knowledge gap — "What is X?" prerequisite.</summary>
    KnowledgeGap,

    /// <summary>Tool invocation node — requires a specific tool (FileSystem, Index, Analyze, etc.).</summary>
    ToolAction
}

public enum QueryNodeStatus
{
    Pending,
    Cached,
    KbSatisfied,
    Executing,
    Complete,
    Failed
}

/// <summary>
/// Content reference extracted from user query (URL, file path, DOI).
/// </summary>
public record ContentReference
{
    public required string Uri { get; init; }
    public ContentReferenceKind Kind { get; init; }
    public string? OriginalText { get; init; }
}

public enum ContentReferenceKind
{
    Url,
    FilePath,
    Doi,
    GitHubReference
}

/// <summary>
/// Temporal scope constraining a sub-query's freshness requirements.
/// </summary>
public record TemporalScope
{
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? OriginalExpression { get; init; }
    public bool RequiresFresh { get; init; }
    public string? TimeSensitivity { get; init; }
}

/// <summary>
/// Result from cache lookup.
/// </summary>
public record CacheHit
{
    public required string CacheKey { get; init; }
    public float Similarity { get; init; }
    public DateTimeOffset CachedAt { get; init; }
    public object? CachedResult { get; init; }
}

/// <summary>
/// Result from knowledge base probe.
/// </summary>
public record KbProbeResult
{
    public KbProbeStatus Status { get; init; }
    public float TopScore { get; init; }
    public int MatchCount { get; init; }
}

public enum KbProbeStatus
{
    /// <summary>KB has no relevant content (score &lt; 0.40).</summary>
    Miss,

    /// <summary>KB has partial content (score 0.40-0.70). Supplement with fetch.</summary>
    Partial,

    /// <summary>KB has good content (score &gt;= 0.70). Can skip/reduce fetch.</summary>
    Satisfied
}

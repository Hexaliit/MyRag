using Mostlylucid.DocSummarizer.Services.Onnx;

namespace LucidRAG.Decomposer.Models;

/// <summary>
/// Aggregated signals from Phase 1 deterministic analysis.
/// Passed to Phase 2 (LLM refinement) and Phase 3 (orchestration).
/// </summary>
public record QuerySignals
{
    /// <summary>Original user query text.</summary>
    public required string OriginalQuery { get; init; }

    /// <summary>Proposed decomposition nodes from structural/entity/temporal analysis.</summary>
    public List<QueryNode> ProposedNodes { get; init; } = [];

    /// <summary>Extracted content references (URLs, files, DOIs).</summary>
    public List<ContentReference> References { get; init; } = [];

    /// <summary>NER entities (passthrough from QueryPreprocessor).</summary>
    public List<NerEntity> Entities { get; init; } = [];

    /// <summary>Recognizer signals (passthrough from TextRecognizerService).</summary>
    public object? RecognizerSignals { get; init; }

    /// <summary>Detected query type (from QueryTypeDetector).</summary>
    public string? DetectedQueryType { get; init; }

    /// <summary>Pre-computed query embedding (for cache + similarity).</summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>
    /// Archetype similarity scores: Comparison, Timeline, Definition, etc.
    /// Replaces regex word-list detection with embedding similarity.
    /// </summary>
    public Dictionary<string, float> ArchetypeScores { get; init; } = new();

    /// <summary>Detected concept type for the overall query.</summary>
    public ConceptType DetectedConcept { get; init; } = ConceptType.General;

    /// <summary>
    /// Query complexity classification for fast-path routing.
    /// Simple queries skip decomposition entirely.
    /// </summary>
    public QueryComplexity Complexity { get; init; } = QueryComplexity.Simple;

    /// <summary>
    /// Detected tool actions from ToolUseAnalyzer.
    /// Each represents a tool invocation needed to fulfill the query.
    /// "Sources are tools" — search, file system, indexer, KB query, crawl.
    /// </summary>
    public List<ToolAction> DetectedTools { get; init; } = [];

    /// <summary>
    /// Embeddings computed during analysis, keyed by text.
    /// Shared across analyzers to avoid re-embedding the same text.
    /// </summary>
    public Dictionary<string, float[]> EmbeddingCache { get; init; } = new();
}

/// <summary>
/// Fast-path complexity classification.
/// Simple queries go straight to single-pass fetch+score.
/// Complex queries get full decomposition + parallel execution.
/// </summary>
public enum QueryComplexity
{
    /// <summary>
    /// Single-topic, no references, no comparisons, no temporal splits.
    /// → Fast path: single sentinel call, single fetch pass.
    /// </summary>
    Simple,

    /// <summary>
    /// Has references or mild decomposition needs.
    /// → Light decomposition: extract references, maybe one split.
    /// </summary>
    Moderate,

    /// <summary>
    /// Multi-topic, comparison, temporal comparison, knowledge gaps.
    /// → Full decomposition: parallel execution, cache, KB routing.
    /// </summary>
    Complex
}

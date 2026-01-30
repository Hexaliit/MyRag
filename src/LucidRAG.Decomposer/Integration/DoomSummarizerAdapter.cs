using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Refinement;

namespace LucidRAG.Decomposer.Integration;

/// <summary>
/// Adapts DoomSummarizer.Core types to decomposer input/output.
/// Keeps the decomposer decoupled from DoomSummarizer-specific types.
///
/// Usage in DoomSummarizer.Core:
///   var input = DoomSummarizerAdapter.ToRefinementInput(sentinelIntent);
///   var result = await pipeline.DecomposeAsync(query, entities, hasUrls, hasDates, input);
///   var enriched = DoomSummarizerAdapter.EnrichPrompt(interpretedPrompt, result);
/// </summary>
public static class DoomSummarizerAdapter
{
    /// <summary>
    /// Convert a SentinelIntent into the decomposer's SentinelRefinementInput.
    /// Called from DoomSummarizer.Core after PromptInterpreter runs.
    /// </summary>
    /// <remarks>
    /// The actual conversion happens in DoomSummarizer.Core where SentinelIntent is accessible.
    /// This method signature shows the expected mapping.
    /// </remarks>
    public static SentinelRefinementInput ToRefinementInput(
        bool isComposite,
        List<string>? subqueries,
        string? correctedQuery,
        List<string>? filterKeywords,
        List<string>? searchQueries,
        List<string>? entities,
        string? timeSensitivity,
        bool requiresFresh,
        string? intent,
        Dictionary<string, double>? categories)
    {
        return new SentinelRefinementInput
        {
            IsComposite = isComposite,
            Subqueries = subqueries,
            CorrectedQuery = correctedQuery,
            FilterKeywords = filterKeywords,
            SearchQueries = searchQueries,
            Entities = entities,
            TimeSensitivity = timeSensitivity,
            RequiresFresh = requiresFresh,
            Intent = intent,
            Categories = categories
        };
    }

    /// <summary>
    /// Extract enrichment data from a DecompositionResult for feeding back
    /// to the existing pipeline.
    /// </summary>
    public static DecompositionEnrichment GetEnrichment(DecompositionResult result)
    {
        return new DecompositionEnrichment
        {
            IsFastPath = result.IsFastPath,
            Complexity = result.Complexity,
            Concept = result.Concept,
            SubQueryNodes = result.Nodes,
            ContentReferences = result.Nodes
                .Where(n => n.Type == QueryNodeType.ContentReference)
                .Select(n => n.Reference!)
                .ToList(),
            PrecomputedEmbeddings = result.Signals?.EmbeddingCache ?? new(),
            QueryEmbedding = result.Signals?.QueryEmbedding,
            LeafCount = result.LeafCount,
            HasPrerequisites = result.Plan.Prerequisites.Count > 0,
            HasToolActions = result.HasToolActions,
            ToolActions = result.Nodes
                .Where(n => n.Type == QueryNodeType.ToolAction && n.ToolAction != null)
                .Select(n => n.ToolAction!)
                .ToList(),
            DetectedTools = result.Signals?.DetectedTools ?? []
        };
    }
}

/// <summary>
/// Enrichment data extracted from decomposition for the existing pipeline.
/// </summary>
public record DecompositionEnrichment
{
    public bool IsFastPath { get; init; }
    public QueryComplexity Complexity { get; init; }
    public ConceptType Concept { get; init; }
    public List<QueryNode> SubQueryNodes { get; init; } = [];
    public List<ContentReference> ContentReferences { get; init; } = [];
    public Dictionary<string, float[]> PrecomputedEmbeddings { get; init; } = new();
    public float[]? QueryEmbedding { get; init; }
    public int LeafCount { get; init; }
    public bool HasPrerequisites { get; init; }

    /// <summary>Whether the query requires tool actions (beyond default search).</summary>
    public bool HasToolActions { get; init; }

    /// <summary>Tool actions detected in the query nodes.</summary>
    public List<ToolAction> ToolActions { get; init; } = [];

    /// <summary>All detected tool signals from the analysis phase.</summary>
    public List<ToolAction> DetectedTools { get; init; } = [];
}

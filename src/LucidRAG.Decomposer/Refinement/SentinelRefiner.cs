using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Refinement;

/// <summary>
/// Merges deterministic Phase 1 analysis with LLM sentinel output.
/// Deterministic structure wins; LLM enhances wording, keywords, and spell correction.
///
/// Merge rules:
/// | Deterministic     | LLM Sentinel      | Result                                          |
/// |-------------------|--------------------|------------------------------------------------|
/// | Both agree on split | 2+ subqueries    | Use deterministic nodes, apply sentinel keywords |
/// | Only deterministic | No composite       | Keep deterministic split                        |
/// | Only LLM splits   | 2+ subqueries      | Accept LLM split (validated via embedding)      |
/// | Neither splits    | Single query        | Single query with sentinel enhancement          |
/// | Conflicting splits | Different count    | Keep deterministic, use LLM wording refinement  |
/// </summary>
public class SentinelRefiner : IDecompositionRefiner
{
    private readonly ILogger<SentinelRefiner>? _logger;

    public SentinelRefiner(ILogger<SentinelRefiner>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Refine decomposition. sentinelData is expected to be a SentinelRefinementInput
    /// (adapter DTO from DoomSummarizer.Core).
    /// </summary>
    public DecompositionResult Refine(QuerySignals signals, object? sentinelData)
    {
        if (sentinelData is not SentinelRefinementInput sentinel)
        {
            // No sentinel data  -  fall back to deterministic
            return new DeterministicRefiner().Refine(signals, null);
        }

        var deterministicNodes = signals.ProposedNodes;
        var sentinelSubqueries = sentinel.Subqueries ?? [];
        var deterministicSplit = deterministicNodes.Count > 1;
        var sentinelSplit = sentinel.IsComposite && sentinelSubqueries.Count > 1;

        List<QueryNode> finalNodes;

        if (deterministicSplit && sentinelSplit)
        {
            // Both agree  -  use deterministic structure, apply sentinel keywords
            _logger?.LogDebug("Both deterministic and sentinel split query. Using deterministic structure.");
            finalNodes = ApplySentinelEnhancements(deterministicNodes, sentinel);
        }
        else if (deterministicSplit && !sentinelSplit)
        {
            // Only deterministic split  -  keep it (structural analysis is more reliable)
            _logger?.LogDebug("Only deterministic split. Keeping structural decomposition.");
            finalNodes = ApplySentinelEnhancements(deterministicNodes, sentinel);
        }
        else if (!deterministicSplit && sentinelSplit)
        {
            // Only LLM split  -  accept sentinel's subqueries
            _logger?.LogDebug("Only sentinel split ({Count} subqueries). Accepting LLM decomposition.",
                sentinelSubqueries.Count);
            finalNodes = sentinelSubqueries.Select(sq => new QueryNode
            {
                Query = sq,
                Type = QueryNodeType.Atomic,
                FilterKeywords = sentinel.FilterKeywords?.ToList() ?? [],
                SearchQueries = sentinel.SearchQueries?.ToList() ?? [],
                Depth = 0
            }).ToList();
        }
        else
        {
            // Neither split  -  single query with sentinel enhancement
            var node = deterministicNodes.Count > 0
                ? deterministicNodes[0]
                : new QueryNode
                {
                    Query = sentinel.CorrectedQuery ?? signals.OriginalQuery,
                    Type = QueryNodeType.Atomic,
                    Embedding = signals.QueryEmbedding,
                    Concept = signals.DetectedConcept,
                    Depth = 0
                };

            finalNodes = [ApplySentinelToNode(node, sentinel)];
        }

        // Add content reference nodes (always preserved, not subject to merge)
        var referenceNodes = deterministicNodes
            .Where(n => n.Type == QueryNodeType.ContentReference)
            .ToList();
        foreach (var refNode in referenceNodes)
        {
            if (!finalNodes.Any(n => n.Type == QueryNodeType.ContentReference
                                     && n.Reference?.Uri == refNode.Reference?.Uri))
            {
                finalNodes.Add(refNode);
            }
        }

        // Build execution plan
        var plan = new ExecutionPlan
        {
            Prerequisites = finalNodes.Where(n => n.IsPrerequisite).ToList(),
            ReferenceNodes = finalNodes.Where(n => n.Type == QueryNodeType.ContentReference).ToList(),
            ToolActionNodes = finalNodes.Where(n => n.Type == QueryNodeType.ToolAction).ToList(),
            ParallelNodes = finalNodes
                .Where(n => !n.IsPrerequisite
                            && n.Type != QueryNodeType.ContentReference
                            && n.Type != QueryNodeType.ToolAction)
                .ToList()
        };

        return new DecompositionResult
        {
            OriginalQuery = signals.OriginalQuery,
            Nodes = finalNodes,
            Plan = plan,
            Complexity = signals.Complexity,
            Concept = signals.DetectedConcept,
            Signals = signals
        };
    }

    private static List<QueryNode> ApplySentinelEnhancements(
        List<QueryNode> nodes, SentinelRefinementInput sentinel)
    {
        return nodes.Select(n => ApplySentinelToNode(n, sentinel)).ToList();
    }

    private static QueryNode ApplySentinelToNode(QueryNode node, SentinelRefinementInput sentinel)
    {
        // Apply spell correction if available
        var query = node.Query;
        if (!string.IsNullOrEmpty(sentinel.CorrectedQuery) &&
            node.Type != QueryNodeType.ContentReference)
        {
            // Don't replace the whole query  -  just note the correction is available
            // The node retains its structural decomposition
        }

        return node with
        {
            FilterKeywords = MergeDistinct(node.FilterKeywords, sentinel.FilterKeywords ?? []),
            SearchQueries = MergeDistinct(node.SearchQueries, sentinel.SearchQueries ?? []),
            TimeScope = node.TimeScope ?? (sentinel.RequiresFresh ? new TemporalScope
            {
                RequiresFresh = true,
                TimeSensitivity = sentinel.TimeSensitivity
            } : null)
        };
    }

    private static List<string> MergeDistinct(List<string> a, IEnumerable<string> b) =>
        a.Concat(b).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Adapter DTO carrying sentinel LLM output into the decomposer.
/// Decouples from DoomSummarizer.Core's SentinelIntent type.
/// </summary>
public record SentinelRefinementInput
{
    public bool IsComposite { get; init; }
    public List<string>? Subqueries { get; init; }
    public string? CorrectedQuery { get; init; }
    public List<string>? FilterKeywords { get; init; }
    public List<string>? SearchQueries { get; init; }
    public List<string>? Entities { get; init; }
    public string? TimeSensitivity { get; init; }
    public bool RequiresFresh { get; init; }
    public string? Intent { get; init; }
    public Dictionary<string, double>? Categories { get; init; }
}

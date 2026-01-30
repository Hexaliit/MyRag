using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Refinement;

/// <summary>
/// Fallback refiner when no LLM is available.
/// Passes Phase 1 output through unchanged — the system works fully without LLM.
/// </summary>
public class DeterministicRefiner : IDecompositionRefiner
{
    private readonly ILogger<DeterministicRefiner>? _logger;

    public DeterministicRefiner(ILogger<DeterministicRefiner>? logger = null)
    {
        _logger = logger;
    }

    public DecompositionResult Refine(QuerySignals signals, object? sentinelData)
    {
        _logger?.LogDebug("Deterministic refiner: no LLM, using Phase 1 output as-is");

        var nodes = signals.ProposedNodes;

        // If no nodes were proposed, create a single atomic node for the whole query
        if (nodes.Count == 0)
        {
            nodes =
            [
                new QueryNode
                {
                    Query = signals.OriginalQuery,
                    Type = QueryNodeType.Atomic,
                    Embedding = signals.QueryEmbedding,
                    Concept = signals.DetectedConcept,
                    Depth = 0
                }
            ];
        }

        var plan = BuildExecutionPlan(nodes);

        return new DecompositionResult
        {
            OriginalQuery = signals.OriginalQuery,
            Nodes = nodes,
            Plan = plan,
            Complexity = signals.Complexity,
            Concept = signals.DetectedConcept,
            Signals = signals
        };
    }

    private static ExecutionPlan BuildExecutionPlan(List<QueryNode> nodes)
    {
        var prerequisites = nodes.Where(n => n.IsPrerequisite).ToList();
        var referenceNodes = nodes.Where(n => n.Type == QueryNodeType.ContentReference).ToList();
        var toolActionNodes = nodes.Where(n => n.Type == QueryNodeType.ToolAction).ToList();
        var parallelNodes = nodes
            .Where(n => !n.IsPrerequisite
                        && n.Type != QueryNodeType.ContentReference
                        && n.Type != QueryNodeType.ToolAction)
            .ToList();

        return new ExecutionPlan
        {
            Prerequisites = prerequisites,
            ReferenceNodes = referenceNodes,
            ToolActionNodes = toolActionNodes,
            ParallelNodes = parallelNodes
        };
    }
}

using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Orchestration;

/// <summary>
/// Prevents infinite decomposition loops.
/// - Max depth: 3 levels
/// - Cycle detection: embedding similarity >= 0.90 to any ancestor
/// - Budget: max 8 total leaf nodes
/// - Timeout: per-node timeout inherited from caller
/// </summary>
public class RecursionGuard
{
    private readonly ILogger<RecursionGuard>? _logger;
    private int _leafCount;

    public int MaxDepth { get; init; } = 3;
    public int MaxLeaves { get; init; } = 8;

    /// <summary>Cosine threshold for cycle detection (ancestor similarity).</summary>
    private const float CycleThreshold = 0.90f;

    public RecursionGuard(ILogger<RecursionGuard>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if a node can be further decomposed.
    /// </summary>
    public bool CanDecompose(QueryNode node, IReadOnlyList<QueryNode>? ancestors = null)
    {
        // Depth check
        if (node.Depth >= MaxDepth)
        {
            _logger?.LogDebug("Recursion guard: max depth {Depth} reached for: {Query}",
                node.Depth, node.Query);
            return false;
        }

        // Budget check
        if (_leafCount >= MaxLeaves)
        {
            _logger?.LogDebug("Recursion guard: leaf budget exhausted ({Count}/{Max})",
                _leafCount, MaxLeaves);
            return false;
        }

        // Cycle detection (embedding similarity to ancestors)
        if (node.Embedding != null && ancestors != null)
        {
            foreach (var ancestor in ancestors)
            {
                if (ancestor.Embedding == null) continue;
                var sim = ComplexityClassifier.CosineSimilarity(node.Embedding, ancestor.Embedding);
                if (sim >= CycleThreshold)
                {
                    _logger?.LogDebug(
                        "Recursion guard: cycle detected (sim={Sim:F3}) between [{Query}] and ancestor [{Ancestor}]",
                        sim, node.Query, ancestor.Query);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Register a leaf node (counts toward budget).
    /// </summary>
    public void RegisterLeaf() => Interlocked.Increment(ref _leafCount);

    /// <summary>Current leaf count.</summary>
    public int CurrentLeafCount => _leafCount;
}

using LucidRAG.Decomposer.Models;

namespace LucidRAG.Decomposer.Refinement;

/// <summary>
/// Interface for Phase 2 refinement. Merges deterministic analysis with LLM sentinel output.
/// </summary>
public interface IDecompositionRefiner
{
    /// <summary>
    /// Refine the decomposition by merging deterministic signals with LLM sentinel output.
    /// </summary>
    /// <param name="signals">Phase 1 deterministic analysis results.</param>
    /// <param name="sentinelData">LLM sentinel output (SentinelIntent as object to avoid coupling).</param>
    DecompositionResult Refine(QuerySignals signals, object? sentinelData);
}

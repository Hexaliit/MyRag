using LucidRAG.Decomposer.Models;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
/// Interface for deterministic query analyzers (Phase 1).
/// Each analyzer examines one aspect of the query and contributes
/// signals/nodes to the aggregated QuerySignals.
/// </summary>
public interface IQueryAnalyzer
{
    /// <summary>
    /// Analyze the query and return signals.
    /// Analyzers are called in sequence but share the same EmbeddingCache
    /// to avoid redundant embedding computations.
    /// </summary>
    Task<QuerySignals> AnalyzeAsync(
        string query,
        QuerySignals existing,
        CancellationToken ct = default);
}

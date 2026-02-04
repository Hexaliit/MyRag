using LucidRAG.Decomposer.Models;

namespace LucidRAG.Decomposer.KnowledgeBase;

/// <summary>
///     Probes the existing knowledge base to determine if a sub-query can be
///     satisfied from existing content (avoiding redundant web fetches).
///     Scoring tiers:
///     - top-3 score >= 0.70: "kb-satisfied" → skip/reduce fetch
///     - top-3 score 0.40-0.70: "kb-partial" → use KB + reduced fetch
///     - top-3 score &lt; 0.40: "kb-miss" → full web fetch needed
/// </summary>
public interface IKnowledgeBaseProbe
{
    /// <summary>
    ///     Probe KB for existing content matching this node's query.
    /// </summary>
    Task<KbProbeResult> ProbeAsync(QueryNode node, CancellationToken ct = default);
}
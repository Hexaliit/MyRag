namespace LucidRAG.Decomposer.Models;

/// <summary>
/// "Concepts are first-class: they select the retrieval plan and the response contract."
///
/// A ConceptPolicy maps a concept type to:
///   - Retrieval strategy (what sources, ranking weights, evidence requirements)
///   - Response contract (shape, required sections, formatters)
///   - Lenses (PII redaction, security mode, ops mode, finance audit)
///   - Trust requirements (citation count, source tier)
///
/// This is the "programmable substrate"  -  the KB stops being documents + search
/// and becomes a set of typed concepts that drive the entire pipeline.
/// </summary>
public record ConceptPolicy
{
    /// <summary>Which concept this policy applies to.</summary>
    public required ConceptType Concept { get; init; }

    // ─── Retrieval Strategy ───

    /// <summary>
    /// Preferred source types for this concept.
    /// e.g., Definition → [canonical, wikipedia]; Incident → [status_pages, changelogs]
    /// </summary>
    public List<string> PreferredSources { get; init; } = [];

    /// <summary>
    /// Source ranking boosts: canonical > community > blog.
    /// Key = source pattern, Value = multiplier.
    /// </summary>
    public Dictionary<string, double> SourceBoosts { get; init; } = new();

    /// <summary>
    /// Minimum evidence items required before synthesizing.
    /// Definition: 1-2 canonical, Comparison: 3+ per side, Policy: 3+ citations.
    /// </summary>
    public int MinEvidenceItems { get; init; } = 1;

    /// <summary>
    /// Whether to prefer the starter corpus / glossary entries first.
    /// True for Definition, Procedure.
    /// </summary>
    public bool PreferCorpusFirst { get; init; }

    /// <summary>
    /// Recency weighting mode.
    /// None = no recency bias, Moderate = slight, Strong = strongly prefer recent.
    /// </summary>
    public RecencyMode Recency { get; init; } = RecencyMode.None;

    /// <summary>
    /// Whether entity graph enrichment should be enabled for this concept.
    /// True for Entity, Architecture, Comparison.
    /// </summary>
    public bool EnableGraphEnrichment { get; init; }

    /// <summary>
    /// Maximum fetch budget (number of items to retrieve).
    /// Lower for Definition (5), higher for Roundup (30).
    /// </summary>
    public int FetchBudget { get; init; } = 20;

    // ─── Response Contract ───

    /// <summary>
    /// Response sections that should be present.
    /// e.g., Procedure → [steps, prerequisites, verification, failure_modes]
    /// </summary>
    public List<string> ResponseSections { get; init; } = [];

    /// <summary>
    /// Response format hint for the synthesis LLM.
    /// e.g., "table", "timeline", "steps", "definition_card", "comparison_table"
    /// </summary>
    public string? ResponseFormat { get; init; }

    /// <summary>
    /// Whether citations are required in the response.
    /// True for Policy, Decision.
    /// </summary>
    public bool RequireCitations { get; init; }

    // ─── Lenses ───

    /// <summary>
    /// Active lenses for this concept. Lenses modify retrieval + output.
    /// Plugins can register custom lenses.
    /// </summary>
    public List<string> ActiveLenses { get; init; } = [];
}

/// <summary>
/// How strongly to weight recency in ranking.
/// </summary>
public enum RecencyMode
{
    /// <summary>No recency bias. Good for Definition, Architecture.</summary>
    None,

    /// <summary>Slight preference for recent content. Good for Entity, Decision.</summary>
    Moderate,

    /// <summary>Strong recency requirement. Good for Roundup, Incident.</summary>
    Strong
}

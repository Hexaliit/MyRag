namespace LucidRAG.Decomposer.Models;

/// <summary>
/// Concept classification driving retrieval strategy and response shape.
/// "Concepts are first-class: they select the retrieval plan and the response contract."
/// Each concept type maps to:
///   - A retrieval strategy (what sources, what ranking, what evidence requirements)
///   - A response renderer (steps, definition card, timeline, options table, etc.)
///   - Optional lenses (PII redaction, security, ops, finance)
/// </summary>
public enum ConceptType
{
    /// <summary>
    /// General/unclassified query. Default retrieval + default formatting.
    /// </summary>
    General,

    /// <summary>
    /// "How do I...", "Steps to...", "Guide for..."
    /// → Steps, prerequisites, hazards, verification checks, common failure modes.
    /// Retrieval: prioritize stepwise sources, official docs, version-matched.
    /// </summary>
    Procedure,

    /// <summary>
    /// "What is X?", "Define...", "Meaning of..."
    /// → Short definition + aliases + "not to be confused with" + examples.
    /// Retrieval: prefer starter corpus / canonical entries first; broaden if missing.
    /// </summary>
    Definition,

    /// <summary>
    /// Entity about a person, org, product, or service.
    /// → Facts card + relationships + timeline + key docs.
    /// Retrieval: NER-driven, entity-specific search, knowledge graph enrichment.
    /// </summary>
    Entity,

    /// <summary>
    /// Incident, outage, or change event.
    /// → Timeline + blast radius + mitigations + current status.
    /// Retrieval: time-bound recency weighting + status pages + changelogs.
    /// </summary>
    Incident,

    /// <summary>
    /// "Should we use X or Y?", "Trade-offs of...", "Pros and cons..."
    /// → Options table + constraints + recommendation + risks.
    /// Retrieval: both sides fetched independently, interleaved results.
    /// </summary>
    Decision,

    /// <summary>
    /// Policy, compliance, or regulatory query.
    /// → Rules + exceptions + citations + redaction lens.
    /// Retrieval: only high-trust sources; require citations; enforce redaction.
    /// </summary>
    Policy,

    /// <summary>
    /// "Error: ...", "Why does X fail?", "Fix for..."
    /// → Diagnosis tree + likely causes + fixes + "collect these logs".
    /// Retrieval: prefer issues/PRs/runbooks; error signatures; dedupe repeats.
    /// </summary>
    Troubleshooting,

    /// <summary>
    /// Architecture, system design, or component query.
    /// → Components + flows + invariants + failure modes.
    /// Retrieval: connective graph scope, cross-document relationships.
    /// </summary>
    Architecture,

    /// <summary>
    /// Timeline/history query: "Evolution of...", "How has X changed?"
    /// → Chronological timeline with events.
    /// Retrieval: temporal comparison mode, multi-period results.
    /// </summary>
    Timeline,

    /// <summary>
    /// Comparison query: "X vs Y", "Difference between..."
    /// → Side-by-side comparison table.
    /// Retrieval: both sides fetched independently, interleaved.
    /// </summary>
    Comparison,

    /// <summary>
    /// News roundup: "Latest on...", "What happened today?"
    /// → Curated news digest.
    /// Retrieval: recency-weighted, multi-source breadth.
    /// </summary>
    Roundup
}

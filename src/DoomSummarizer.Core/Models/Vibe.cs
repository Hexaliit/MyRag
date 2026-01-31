namespace DoomSummarizer.Models;

/// <summary>
/// A resolved vibe — the unified personality/tone configuration that drives
/// both DoomSummarizer synthesis and LucidRAG lens presentation.
///
/// A vibe can originate from:
///   1. A .lens.yaml file (full lens with personality, scoring, templates)
///   2. The vibes dictionary in DoomConfig (simple name → prompt mapping)
///   3. Arbitrary user text (e.g., --vibe "excited about space")
///
/// When sourced from a lens, the vibe carries the full personality configuration.
/// This closes the loop between DoomSummarizer vibes and LucidRAG lenses:
/// a vibe IS the personality facet of a lens.
/// </summary>
public record Vibe
{
    /// <summary>
    /// Vibe identifier (e.g., "doom", "friendly", "blog", "technical").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The prompt instruction injected as VIBE_PROMPT into synthesis templates.
    /// Derived from lens personality or config vibes dictionary.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Search query qualifier prepended to queries for sentiment-aligned results.
    /// e.g., "doom" → "concerning problems risks issues in"
    /// </summary>
    public string? SearchQualifier { get; init; }

    /// <summary>
    /// Representative text for cosine-similarity-based sentiment scoring.
    /// Used as an embedding anchor to bias result ranking toward the vibe.
    /// </summary>
    public string? RepresentativeText { get; init; }

    /// <summary>
    /// The lens ID this vibe was resolved from, if any.
    /// Null when resolved from config vibes dictionary or custom text.
    /// </summary>
    public string? LensId { get; init; }

    /// <summary>
    /// Personality tone from the lens (e.g., "friendly", "formal", "technical").
    /// </summary>
    public string? Tone { get; init; }

    /// <summary>
    /// Spelling variant preference (e.g., "british", "american").
    /// </summary>
    public string? SpellingVariant { get; init; }

    /// <summary>
    /// Persona description (e.g., "a knowledgeable tech writer").
    /// </summary>
    public string? Persona { get; init; }

    /// <summary>
    /// Style notes for the assistant's communication.
    /// </summary>
    public IReadOnlyList<string>? StyleNotes { get; init; }

    /// <summary>
    /// Phrase substitution preferences (avoid → use).
    /// </summary>
    public IReadOnlyDictionary<string, string>? PhrasePreferences { get; init; }

    /// <summary>
    /// Whether this vibe was resolved from a lens file (vs. config or custom text).
    /// </summary>
    public bool IsFromLens => LensId != null;
}

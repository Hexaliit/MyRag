namespace LucidRAG.Lenses;

/// <summary>
/// Manifest metadata for a lens package.
/// Defines how the lens customizes search results and synthesis.
/// </summary>
public record LensManifest(
    string Id,
    string Name,
    string Description,
    string Version,
    string? Author,
    int Priority,
    LensScoringConfig Scoring,
    LensTemplatesConfig Templates,
    string? Styles,
    Dictionary<string, object>? Settings,
    LensPersonality? Personality = null
);

/// <summary>
/// Personality configuration for a lens.
/// Controls tone, language preferences, and response style.
/// </summary>
public record LensPersonality(
    /// <summary>
    /// Tone of responses: friendly, formal, casual, technical, etc.
    /// </summary>
    string? Tone = null,

    /// <summary>
    /// Spelling variant: british, american, australian, etc.
    /// </summary>
    string? SpellingVariant = null,

    /// <summary>
    /// Specific persona to embody (e.g., "helpful librarian", "tech enthusiast").
    /// </summary>
    string? Persona = null,

    /// <summary>
    /// Communication style preferences (e.g., use bullet points, be concise).
    /// </summary>
    List<string>? StyleNotes = null,

    /// <summary>
    /// Phrases to use or avoid.
    /// </summary>
    Dictionary<string, string>? PhrasePreferences = null
);

/// <summary>
/// Scoring weights for Reciprocal Rank Fusion (RRF).
/// Controls how different signals are weighted in result ranking.
/// </summary>
public record LensScoringConfig(
    double DenseWeight,
    double Bm25Weight,
    double SalienceWeight,
    double FreshnessWeight
);

/// <summary>
/// Template file paths within the lens package.
/// All paths are relative to the lens package directory.
/// </summary>
public record LensTemplatesConfig(
    string SystemPrompt,
    string Citation,
    string? Response
);

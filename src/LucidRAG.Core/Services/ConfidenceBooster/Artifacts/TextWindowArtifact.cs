namespace LucidRAG.Core.Services.ConfidenceBooster.Artifacts;

/// <summary>
///     Artifact representing a bounded text window for LLM-based OCR refinement or entity extraction.
/// </summary>
public class TextWindowArtifact : IArtifact
{
    /// <summary>
    ///     The text window to refine (from OCR, parsing, etc.).
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    ///     Line numbers or character offsets [start, end].
    /// </summary>
    public int[]? LineRange { get; init; }

    public int[]? CharacterRange { get; init; }

    /// <summary>
    ///     Context from surrounding text (for disambiguation).
    /// </summary>
    public string? ContextBefore { get; init; }

    public string? ContextAfter { get; init; }

    /// <summary>
    ///     Original source (ocr, pdf_parse, html_parse, etc.).
    /// </summary>
    public string? SourceType { get; init; }

    /// <summary>
    ///     Task type for LLM (ocr_refinement, entity_extraction, date_normalization, technical_term_correction).
    /// </summary>
    public required string TaskType { get; init; }

    public required string ArtifactId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string SignalName { get; init; }
    public required double OriginalConfidence { get; init; }
    public string ArtifactType => "text_window";
    public Dictionary<string, object> Metadata { get; init; } = new();
}
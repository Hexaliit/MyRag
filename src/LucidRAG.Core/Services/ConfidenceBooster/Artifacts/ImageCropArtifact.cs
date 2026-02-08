namespace LucidRAG.Core.Services.ConfidenceBooster.Artifacts;

/// <summary>
///     Artifact representing a bounded image crop for LLM-based object recognition or OCR refinement.
/// </summary>
public class ImageCropArtifact : IArtifact
{
    /// <summary>
    ///     Base64-encoded cropped image (PNG or JPEG).
    /// </summary>
    public required string Base64Image { get; init; }

    /// <summary>
    ///     Bounding box that created this crop [x, y, width, height].
    /// </summary>
    public required int[] BoundingBox { get; init; }

    /// <summary>
    ///     Frame number (for video frames or multi-page PDFs).
    /// </summary>
    public int? FrameNumber { get; init; }

    /// <summary>
    ///     Original low-confidence classification (e.g., "person", "text", "table").
    /// </summary>
    public string? OriginalClassification { get; init; }

    /// <summary>
    ///     Task type for LLM (object_recognition, ocr, chart_extraction, etc.).
    /// </summary>
    public required string TaskType { get; init; }

    public required string ArtifactId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string SignalName { get; init; }
    public required double OriginalConfidence { get; init; }
    public string ArtifactType => "image_crop";
    public Dictionary<string, object> Metadata { get; init; } = new();
}
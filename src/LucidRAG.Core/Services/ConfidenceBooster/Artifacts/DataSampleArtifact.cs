namespace LucidRAG.Core.Services.ConfidenceBooster.Artifacts;

/// <summary>
///     Artifact representing sample data records for LLM-based schema refinement or type inference.
/// </summary>
public class DataSampleArtifact : IArtifact
{
    /// <summary>
    ///     Sample records from the dataset (JSON array).
    /// </summary>
    public required string SampleRecords { get; init; }

    /// <summary>
    ///     Column/field name being analyzed.
    /// </summary>
    public string? ColumnName { get; init; }

    /// <summary>
    ///     Original low-confidence type inference (e.g., "string", "date", "numeric").
    /// </summary>
    public string? OriginalType { get; init; }

    /// <summary>
    ///     Statistical profile of the column (distinct count, nulls, min/max, etc.).
    /// </summary>
    public Dictionary<string, object>? StatisticalProfile { get; init; }

    /// <summary>
    ///     Task type for LLM (type_inference, format_detection, semantic_classification).
    /// </summary>
    public required string TaskType { get; init; }

    public required string ArtifactId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string SignalName { get; init; }
    public required double OriginalConfidence { get; init; }
    public string ArtifactType => "data_sample";
    public Dictionary<string, object> Metadata { get; init; } = new();
}
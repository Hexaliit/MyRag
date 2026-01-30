namespace Mostlylucid.Storage.Core.Abstractions.Models;

/// <summary>
/// Vector document with additional named vectors for multi-modal storage.
/// Extends VectorDocument with named vector support (e.g., "voice", "visual", "color", "motion").
/// The primary Embedding field remains the default search vector.
/// </summary>
public class MultiVectorDocument : VectorDocument
{
    /// <summary>
    /// Named vectors keyed by name (e.g., "voice", "visual").
    /// The primary <see cref="VectorDocument.Embedding"/> remains the default search vector.
    /// </summary>
    public Dictionary<string, float[]> NamedVectors { get; set; } = new();
}

/// <summary>
/// Configuration for a named vector within a multi-vector collection.
/// </summary>
public class NamedVectorConfig
{
    /// <summary>
    /// Vector name (e.g., "voice", "visual", "color", "motion").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Vector dimension (e.g., 192, 512, 384).
    /// </summary>
    public required int Dimension { get; set; }

    /// <summary>
    /// Distance metric for this named vector.
    /// </summary>
    public VectorDistance DistanceMetric { get; set; } = VectorDistance.Cosine;
}

/// <summary>
/// Search query targeting a specific named vector.
/// </summary>
public class MultiVectorSearchQuery : VectorSearchQuery
{
    /// <summary>
    /// Which named vector to search against.
    /// Null means use the default <see cref="VectorDocument.Embedding"/>.
    /// </summary>
    public string? VectorName { get; set; }
}

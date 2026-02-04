using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LucidRAG.Entities;

/// <summary>
///     Represents a link between two segments within a document.
///     Enables intra-document graph traversal for improved retrieval.
/// </summary>
public class SegmentLink
{
    [Key] public Guid Id { get; set; }

    /// <summary>
    ///     Document containing both segments.
    /// </summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    ///     Source segment hash (from vector store).
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string SourceSegmentHash { get; set; }

    /// <summary>
    ///     Target segment hash (from vector store).
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string TargetSegmentHash { get; set; }

    /// <summary>
    ///     Link type: sequential, reference, semantic, heading, etc.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public required string LinkType { get; set; }

    /// <summary>
    ///     Link strength/weight (0-1).
    ///     Higher = stronger connection.
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    ///     Additional link metadata (JSON).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public DocumentEntity? Document { get; set; }
}

/// <summary>
///     Types of segment links within a document.
/// </summary>
public static class SegmentLinkTypes
{
    /// <summary>Adjacent segments in document order.</summary>
    public const string Sequential = "sequential";

    /// <summary>Shared heading/section hierarchy.</summary>
    public const string Heading = "heading";

    /// <summary>High semantic similarity (>0.8).</summary>
    public const string Semantic = "semantic";

    /// <summary>Explicit reference (link, citation).</summary>
    public const string Reference = "reference";

    /// <summary>Shared entity mentions.</summary>
    public const string Entity = "entity";

    /// <summary>Co-occurrence in same page/section.</summary>
    public const string CoLocation = "colocation";
}
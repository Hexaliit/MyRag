using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace LucidRAG.Entities;

/// <summary>
///     Stores semantic embeddings for extracted features (entities, terms, concepts).
///     Uses pgvector for efficient similarity search directly in PostgreSQL.
///     Use cases:
///     - "yellow" → "gold", "amber", "lemon" (color similarity)
///     - "machine learning" → "deep learning", "neural networks" (concept similarity)
///     - Entity deduplication across documents
/// </summary>
public class FeatureEmbedding
{
    [Key] public Guid Id { get; set; }

    /// <summary>
    ///     Collection this feature belongs to (for collection-scoped queries).
    /// </summary>
    public Guid? CollectionId { get; set; }

    /// <summary>
    ///     The canonical feature text (entity name, term, etc.)
    /// </summary>
    [Required]
    [MaxLength(512)]
    public required string FeatureText { get; set; }

    /// <summary>
    ///     Normalized text for exact matching (lowercase, trimmed).
    /// </summary>
    [Required]
    [MaxLength(512)]
    public required string NormalizedText { get; set; }

    /// <summary>
    ///     Feature type: entity, term, concept, date, location, etc.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string FeatureType { get; set; }

    /// <summary>
    ///     The embedding vector stored as pgvector type.
    ///     384 dimensions for all-MiniLM-L6-v2.
    /// </summary>
    [Column(TypeName = "vector(384)")]
    public Vector? Embedding { get; set; }

    /// <summary>
    ///     Embedding model used (for versioning).
    /// </summary>
    [MaxLength(128)]
    public string? EmbeddingModel { get; set; }

    /// <summary>
    ///     Number of documents containing this feature.
    /// </summary>
    public int DocumentCount { get; set; }

    /// <summary>
    ///     Total occurrence count across all documents.
    /// </summary>
    public int OccurrenceCount { get; set; }

    /// <summary>
    ///     Additional metadata (JSON).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public CollectionEntity? Collection { get; set; }
}

/// <summary>
///     Feature types for categorization.
/// </summary>
public static class FeatureTypes
{
    public const string Entity = "entity"; // Named entity (person, org, location)
    public const string Term = "term"; // Salient term/phrase
    public const string Concept = "concept"; // Abstract concept
    public const string Date = "date"; // Temporal reference
    public const string Location = "location"; // Geographic reference
    public const string Person = "person"; // Person name
    public const string Organization = "org"; // Organization name
    public const string Product = "product"; // Product/brand name
    public const string Code = "code"; // Code identifier (class, function)
    public const string Color = "color"; // Color reference
    public const string Number = "number"; // Numeric value
}
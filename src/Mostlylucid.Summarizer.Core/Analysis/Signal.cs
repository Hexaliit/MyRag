namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
/// Atomic unit of analytical information produced by a wave.
/// Signals carry a typed value, confidence, provenance, and optional metadata.
/// Key convention: "domain.category.metric" (e.g., "doc.structure.heading_count").
/// </summary>
public record Signal
{
    /// <summary>
    /// Unique key identifying what this signal measures.
    /// Convention: "domain.category.metric"
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The measured value. Can be any serializable type.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Confidence score (0.0 - 1.0) indicating reliability.
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Source wave that produced this signal.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// When this signal was generated.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional metadata about computation.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Optional tags for categorization.
    /// </summary>
    public List<string>? Tags { get; init; }

    /// <summary>
    /// Get typed value or default.
    /// </summary>
    public T? GetValue<T>() => Value is T typed ? typed : default;
}

/// <summary>
/// Aggregation strategy for combining multiple signals for the same key.
/// </summary>
public enum AggregationStrategy
{
    HighestConfidence,
    MostRecent,
    WeightedAverage,
    Collect,
    MajorityVote,
    Custom
}

/// <summary>
/// Standard signal tags across domains.
/// </summary>
public static class SignalTags
{
    public const string Structure = "structure";
    public const string Content = "content";
    public const string Semantic = "semantic";
    public const string Lexical = "lexical";
    public const string Entity = "entity";
    public const string Topic = "topic";
    public const string Quality = "quality";
    public const string Metadata = "metadata";
    public const string Embedding = "embedding";
    public const string Position = "position";
    public const string Visual = "visual";
    public const string Color = "color";
    public const string Forensic = "forensic";
    public const string Identity = "identity";
}

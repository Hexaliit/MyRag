namespace LucidSupport.Models;

/// <summary>
///     Maps a user question pattern to a knowledge base article ID.
/// </summary>
public sealed record TopicMapping
{
    /// <summary>The question text or pattern (e.g., "What payment methods do you accept?").</summary>
    public required string Question { get; init; }

    /// <summary>Knowledge base article ID to link to.</summary>
    public required string ArticleId { get; init; }
}

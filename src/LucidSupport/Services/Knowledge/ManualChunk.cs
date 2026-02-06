namespace LucidSupport.Services.Knowledge;

/// <summary>
///     A chunk of text from an ingested manual/knowledge document.
/// </summary>
public sealed record ManualChunk
{
    /// <summary>Unique chunk identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Chunk text content.</summary>
    public required string Text { get; init; }

    /// <summary>Source file path.</summary>
    public required string SourceFile { get; init; }

    /// <summary>Section heading this chunk came from (if any).</summary>
    public string? Section { get; init; }

    /// <summary>Embedding vector.</summary>
    public required float[] Embedding { get; init; }
}

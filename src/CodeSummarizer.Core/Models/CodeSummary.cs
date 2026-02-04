namespace CodeSummarizer.Models;

/// <summary>
///     Summary of a code block: structural info from AST + optional semantic description.
/// </summary>
public record CodeSummary
{
    /// <summary>One-line description suitable for embedding and segment display.</summary>
    public required string Description { get; init; }

    /// <summary>Language of the code block (as specified in the markdown fence).</summary>
    public required string Language { get; init; }

    /// <summary>Function/method signatures extracted from AST.</summary>
    public IReadOnlyList<string> Functions { get; init; } = [];

    /// <summary>Class/struct/interface definitions extracted from AST.</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>Import/using statements extracted from AST.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>Source of the description: "tree-sitter", "llm", "heuristic".</summary>
    public required string Source { get; init; }

    /// <summary>Original code text (for citation/reference).</summary>
    public required string OriginalCode { get; init; }
}
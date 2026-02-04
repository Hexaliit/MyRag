namespace CodeSummarizer.Models;

/// <summary>
///     Summary of a Mermaid diagram: parsed structure + optional semantic description.
/// </summary>
public record MermaidSummary
{
    /// <summary>Natural language description of the diagram.</summary>
    public required string Description { get; init; }

    /// <summary>Diagram type: flowchart, sequence, class, state, er, gantt, pie, unknown.</summary>
    public required string DiagramType { get; init; }

    /// <summary>Extracted node/participant names.</summary>
    public IReadOnlyList<string> Nodes { get; init; } = [];

    /// <summary>Extracted relationships as "A -> B" strings.</summary>
    public IReadOnlyList<string> Edges { get; init; } = [];

    /// <summary>Source of the description: "parser", "llm", "heuristic".</summary>
    public required string Source { get; init; }

    /// <summary>Original mermaid code text.</summary>
    public required string OriginalCode { get; init; }
}
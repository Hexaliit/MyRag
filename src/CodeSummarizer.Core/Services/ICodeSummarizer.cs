using CodeSummarizer.Models;

namespace CodeSummarizer.Services;

/// <summary>
///     Summarizes code blocks and Mermaid diagrams for RAG embeddings.
///     Uses Tree-sitter for structural AST parsing, optional LLM for semantic descriptions.
/// </summary>
public interface ICodeSummarizer
{
    /// <summary>
    ///     Summarize a code block. Returns structural + optional semantic summary.
    /// </summary>
    /// <param name="code">The code text (without fence markers).</param>
    /// <param name="language">The fence language (e.g., "csharp", "python", "js").</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CodeSummary> SummarizeCodeAsync(string code, string language, CancellationToken ct = default);

    /// <summary>
    ///     Summarize a mermaid diagram. Returns diagram description with nodes/edges.
    /// </summary>
    /// <param name="mermaidCode">The mermaid code (without fence markers).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MermaidSummary> SummarizeMermaidAsync(string mermaidCode, CancellationToken ct = default);

    /// <summary>
    ///     Quick check: is this language supported for AST parsing?
    /// </summary>
    bool IsLanguageSupported(string language);
}

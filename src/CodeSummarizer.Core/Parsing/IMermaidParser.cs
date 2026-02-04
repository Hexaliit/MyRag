using CodeSummarizer.Models;

namespace CodeSummarizer.Parsing;

/// <summary>
///     Abstraction for mermaid diagram parsing.
///     Default implementation uses regex; can be overridden with Jint-based parser.
/// </summary>
public interface IMermaidParser
{
    /// <summary>
    ///     Parse mermaid code and return a structural summary.
    /// </summary>
    MermaidSummary Parse(string mermaidCode);
}
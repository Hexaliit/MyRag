using CodeSummarizer.Models;

namespace CodeSummarizer.Parsing;

/// <summary>
///     Regex-based implementation of <see cref="IMermaidParser" />.
///     Delegates to the existing static <see cref="MermaidParser" /> class.
/// </summary>
public sealed class RegexMermaidParser : IMermaidParser
{
    /// <inheritdoc />
    public MermaidSummary Parse(string mermaidCode)
    {
        return MermaidParser.Parse(mermaidCode);
    }
}
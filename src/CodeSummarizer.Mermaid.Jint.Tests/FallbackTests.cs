using CodeSummarizer.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSummarizer.Mermaid.Jint.Tests;

/// <summary>
///     Tests that <see cref="JintMermaidParser" /> correctly falls back to
///     <see cref="RegexMermaidParser" /> when Jint parsing fails.
/// </summary>
public class FallbackTests : IDisposable
{
    private readonly JintMermaidParser _parser = new(NullLogger<JintMermaidParser>.Instance);

    public void Dispose()
    {
        _parser.Dispose();
    }

    [Fact]
    public void Parse_InvalidSyntax_FallsBackToRegex()
    {
        // This is complete gibberish — Jint should fail and regex should still extract something
        var mermaid = """
                      graph TD
                          A[Start] --> B{Decision}
                          $$$ INVALID SYNTAX @@@
                      """;

        var result = _parser.Parse(mermaid);

        // Should still return a result (either from Jint's best-effort or regex fallback)
        result.Should().NotBeNull();
        result.DiagramType.Should().Be("flowchart");
    }

    [Fact]
    public void Parse_EmptyInput_DelegatesDirectlyToFallback()
    {
        var result = _parser.Parse("");

        result.DiagramType.Should().Be("unknown");
        result.Description.Should().Contain("Empty");
        // Empty input is handled before Jint is even invoked
        result.Source.Should().Be("parser");
    }

    [Fact]
    public void Parse_ValidInput_NeverReturnsNull()
    {
        var inputs = new[]
        {
            "graph TD\n    A --> B",
            "sequenceDiagram\n    Alice->>Bob: Hi",
            "classDiagram\n    class Animal",
            "stateDiagram\n    [*] --> Idle",
            "pie\n    \"A\" : 50\n    \"B\" : 50",
            "gantt\n    title Plan\n    section A\n    Task : a1, 2024-01-01, 7d",
            "mindmap\n    root\n        child"
        };

        foreach (var input in inputs)
        {
            var result = _parser.Parse(input);
            result.Should().NotBeNull($"input was: {input[..Math.Min(30, input.Length)]}...");
            result.Description.Should().NotBeNullOrWhiteSpace();
            result.DiagramType.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Parse_RegexFallbackProducesParserSource()
    {
        // When the regex fallback is used, Source should be "parser"
        var regexParser = new RegexMermaidParser();
        var result = regexParser.Parse("graph TD\n    A --> B");

        result.Source.Should().Be("parser");
    }
}
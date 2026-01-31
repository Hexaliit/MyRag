using CodeSummarizer.Mermaid.Jint;
using CodeSummarizer.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSummarizer.Mermaid.Jint.Tests;

/// <summary>
///     Tests for <see cref="JintMermaidParser"/> — parity with regex parser
///     and compliance with mermaid.js spec.
/// </summary>
public class JintMermaidParserTests : IDisposable
{
    private readonly JintMermaidParser _parser = new(NullLogger<JintMermaidParser>.Instance);

    public void Dispose() => _parser.Dispose();

    // === Parity tests: same inputs as MermaidParserTests → same DiagramType ===

    [Fact]
    public void Parse_Flowchart_DetectsDiagramType()
    {
        var mermaid = """
            graph TD
                A[Start] --> B{Decision}
                B -->|Yes| C[Process]
                B -->|No| D[End]
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("flowchart");
        result.Description.Should().NotBeNullOrWhiteSpace();
        result.OriginalCode.Should().Be(mermaid);
    }

    [Fact]
    public void Parse_FlowchartLR_DetectsDiagramType()
    {
        var mermaid = """
            flowchart LR
                A --> B --> C
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("flowchart");
    }

    [Fact]
    public void Parse_SequenceDiagram_DetectsDiagramType()
    {
        var mermaid = """
            sequenceDiagram
                participant Alice
                participant Bob
                Alice->>Bob: Hello Bob
                Bob-->>Alice: Hi Alice
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("sequence");
    }

    [Fact]
    public void Parse_ClassDiagram_DetectsDiagramType()
    {
        var mermaid = """
            classDiagram
                class Animal
                class Duck
                Animal <|-- Duck
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("class");
    }

    [Fact]
    public void Parse_StateDiagram_DetectsDiagramType()
    {
        var mermaid = """
            stateDiagram
                [*] --> Idle
                Idle --> Processing
                Processing --> Done
                Done --> [*]
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("state");
    }

    [Fact]
    public void Parse_Pie_DetectsDiagramType()
    {
        var mermaid = """
            pie title Browser Market Share
                "Chrome" : 65
                "Firefox" : 15
                "Safari" : 12
                "Other" : 8
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("pie");
    }

    [Fact]
    public void Parse_Gantt_DetectsDiagramType()
    {
        var mermaid = """
            gantt
                title Project Plan
                section Design
                    Wireframes : a1, 2024-01-01, 7d
                    Mockups : a2, after a1, 5d
                section Development
                    Backend : b1, 2024-01-15, 14d
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("gantt");
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsUnknown()
    {
        var result = _parser.Parse("");

        result.DiagramType.Should().Be("unknown");
        result.Description.Should().Contain("Empty");
    }

    [Fact]
    public void Parse_NullInput_ReturnsUnknown()
    {
        var result = _parser.Parse(null!);

        result.DiagramType.Should().Be("unknown");
    }

    [Fact]
    public void Parse_WhitespaceInput_ReturnsUnknown()
    {
        var result = _parser.Parse("   \n\t  ");

        result.DiagramType.Should().Be("unknown");
    }

    [Fact]
    public void Parse_Mindmap_DetectsDiagramType()
    {
        var mermaid = """
            mindmap
                root((Root))
                    Child1
                    Child2
            """;

        var result = _parser.Parse(mermaid);

        result.DiagramType.Should().Be("mindmap");
    }

    // === Source field tests ===

    [Fact]
    public void Parse_ValidDiagram_SourceIsJintOrParser()
    {
        var mermaid = """
            graph TD
                A --> B
            """;

        var result = _parser.Parse(mermaid);

        // Source should be "jint" if mermaid.js loaded, or "parser" if it fell back to regex
        result.Source.Should().BeOneOf("jint", "parser");
    }

    // === Node/Edge extraction tests ===

    [Fact]
    public void Parse_Flowchart_ExtractsNodesAndEdges()
    {
        var mermaid = """
            graph TD
                A[Start] --> B{Decision}
                B -->|Yes| C[Process]
                B -->|No| D[End]
            """;

        var result = _parser.Parse(mermaid);

        result.Nodes.Should().NotBeEmpty();
        result.Edges.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_SequenceDiagram_ExtractsParticipants()
    {
        var mermaid = """
            sequenceDiagram
                participant Alice
                participant Bob
                Alice->>Bob: Hello Bob
                Bob-->>Alice: Hi Alice
            """;

        var result = _parser.Parse(mermaid);

        result.Nodes.Should().NotBeEmpty();
        result.Edges.Should().NotBeEmpty();
    }
}

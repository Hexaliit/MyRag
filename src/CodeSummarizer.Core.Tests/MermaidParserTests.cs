using CodeSummarizer.Parsing;

namespace CodeSummarizer.Core.Tests;

public class MermaidParserTests
{
    [Fact]
    public void Parse_Flowchart_ExtractsNodesAndEdges()
    {
        var mermaid = """
            graph TD
                A[Start] --> B{Decision}
                B -->|Yes| C[Process]
                B -->|No| D[End]
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("flowchart");
        result.Source.Should().Be("parser");
        result.Nodes.Should().NotBeEmpty();
        result.Edges.Should().NotBeEmpty();
        result.Description.Should().Contain("Flowchart");
    }

    [Fact]
    public void Parse_FlowchartLR_DetectsDirection()
    {
        var mermaid = """
            flowchart LR
                A --> B --> C
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("flowchart");
        result.Description.Should().Contain("left-right");
    }

    [Fact]
    public void Parse_SequenceDiagram_ExtractsParticipantsAndMessages()
    {
        var mermaid = """
            sequenceDiagram
                participant Alice
                participant Bob
                Alice->>Bob: Hello Bob
                Bob-->>Alice: Hi Alice
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("sequence");
        result.Nodes.Should().Contain("Alice");
        result.Nodes.Should().Contain("Bob");
        result.Edges.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Description.Should().Contain("2 participants");
    }

    [Fact]
    public void Parse_ClassDiagram_ExtractsClassesAndRelationships()
    {
        var mermaid = """
            classDiagram
                class Animal
                class Duck
                Animal <|-- Duck
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("class");
        result.Nodes.Should().Contain("Animal");
        result.Nodes.Should().Contain("Duck");
        result.Edges.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_StateDiagram_ExtractsStatesAndTransitions()
    {
        var mermaid = """
            stateDiagram
                [*] --> Idle
                Idle --> Processing
                Processing --> Done
                Done --> [*]
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("state");
        result.Nodes.Should().Contain("Idle");
        result.Nodes.Should().Contain("Processing");
        result.Nodes.Should().Contain("Done");
        result.Edges.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Parse_Pie_ExtractsSlices()
    {
        var mermaid = """
            pie title Browser Market Share
                "Chrome" : 65
                "Firefox" : 15
                "Safari" : 12
                "Other" : 8
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("pie");
        result.Nodes.Should().HaveCount(4);
        result.Description.Should().Contain("Browser Market Share");
        result.Description.Should().Contain("4 slices");
    }

    [Fact]
    public void Parse_Gantt_ExtractsSectionsAndTasks()
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

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("gantt");
        result.Description.Should().Contain("Project Plan");
        result.Description.Should().Contain("2 sections");
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsUnknown()
    {
        var result = MermaidParser.Parse("");

        result.DiagramType.Should().Be("unknown");
        result.Description.Should().Contain("Empty");
    }

    [Fact]
    public void Parse_UnrecognizedType_ReturnsGeneric()
    {
        var mermaid = """
            mindmap
                root((Root))
                    Child1
                    Child2
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("mindmap");
        result.Source.Should().Be("parser");
    }

    [Fact]
    public void Parse_FlowchartWithSubgraphs_DetectsSubgraphs()
    {
        var mermaid = """
            graph TD
                subgraph Backend
                    A[API] --> B[Database]
                end
                subgraph Frontend
                    C[UI] --> A
                end
            """;

        var result = MermaidParser.Parse(mermaid);

        result.DiagramType.Should().Be("flowchart");
        result.Nodes.Should().Contain(n => n.Contains("Backend"));
        result.Nodes.Should().Contain(n => n.Contains("Frontend"));
    }
}

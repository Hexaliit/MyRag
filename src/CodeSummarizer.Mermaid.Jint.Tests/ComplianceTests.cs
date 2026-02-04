using CodeSummarizer.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSummarizer.Mermaid.Jint.Tests;

/// <summary>
///     Compliance tests for edge cases that the regex parser may miss or misparse.
///     The Jint parser should handle these correctly since it runs actual mermaid.js.
/// </summary>
public class ComplianceTests : IDisposable
{
    private readonly JintMermaidParser _jint = new(NullLogger<JintMermaidParser>.Instance);
    private readonly RegexMermaidParser _regex = new();

    public void Dispose()
    {
        _jint.Dispose();
    }

    // === Complex node shapes ===

    [Fact]
    public void Parse_FlowchartWithStadiumShape_ParsesCorrectly()
    {
        // Stadium-shaped node: ([label])
        var mermaid = """
                      flowchart LR
                          A([Start]) --> B[Process] --> C([End])
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
        result.Should().NotBeNull();
    }

    [Fact]
    public void Parse_FlowchartWithHexagonShape_ParsesCorrectly()
    {
        // Hexagon node: {{label}}
        var mermaid = """
                      flowchart TD
                          A{{Decision}} --> B[Process]
                          A --> C[Alternative]
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
    }

    [Fact]
    public void Parse_FlowchartWithSubroutineShape_ParsesCorrectly()
    {
        // Subroutine node: [[label]]
        var mermaid = """
                      flowchart TD
                          A[Start] --> B[[Subroutine]]
                          B --> C[End]
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
    }

    [Fact]
    public void Parse_FlowchartWithDatabaseShape_ParsesCorrectly()
    {
        // Database/cylinder: [(label)]
        var mermaid = """
                      flowchart LR
                          A[App] --> B[(Database)]
                          B --> C[Report]
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
    }

    // === Bidirectional and dotted links ===

    [Fact]
    public void Parse_FlowchartWithBidirectionalLink_ParsesCorrectly()
    {
        var mermaid = """
                      flowchart LR
                          A <--> B
                          B <-.-> C
                          C <==> D
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
    }

    [Fact]
    public void Parse_FlowchartWithLinkText_ParsesCorrectly()
    {
        var mermaid = """
                      flowchart LR
                          A -- text --> B
                          A ---|text| B
                          A -->|text| B
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
    }

    // === Nested subgraphs ===

    [Fact]
    public void Parse_FlowchartWithNestedSubgraphs_ParsesCorrectly()
    {
        var mermaid = """
                      flowchart TB
                          subgraph Outer
                              subgraph Inner
                                  A[Node A] --> B[Node B]
                              end
                              B --> C[Node C]
                          end
                          C --> D[Node D]
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("flowchart");
    }

    // === Sequence diagram edge cases ===

    [Fact]
    public void Parse_SequenceWithActivation_ParsesCorrectly()
    {
        var mermaid = """
                      sequenceDiagram
                          Alice->>+John: Hello John
                          John-->>-Alice: Great!
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("sequence");
    }

    [Fact]
    public void Parse_SequenceWithNotes_ParsesCorrectly()
    {
        var mermaid = """
                      sequenceDiagram
                          participant Alice
                          participant Bob
                          Note right of Alice: Alice thinks
                          Alice->>Bob: Hello Bob
                          Note over Alice,Bob: Both pause
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("sequence");
    }

    [Fact]
    public void Parse_SequenceWithLoopAndAlt_ParsesCorrectly()
    {
        var mermaid = """
                      sequenceDiagram
                          Alice->>Bob: Hello Bob
                          loop Every minute
                              Bob->>Alice: Ping
                          end
                          alt is sick
                              Bob->>Alice: Not well
                          else is well
                              Bob->>Alice: Feeling fine
                          end
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("sequence");
    }

    // === Class diagram edge cases ===

    [Fact]
    public void Parse_ClassWithMembers_ParsesCorrectly()
    {
        var mermaid = """
                      classDiagram
                          class BankAccount {
                              +String owner
                              +Bigdecimal balance
                              +deposit(amount)
                              +withdrawal(amount)
                          }
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("class");
    }

    [Fact]
    public void Parse_ClassWithGenericTypes_ParsesCorrectly()
    {
        var mermaid = """
                      classDiagram
                          class Square~Shape~ {
                              int id
                              List~int~ position
                              setPoints(List~int~ points)
                              getPoints() List~int~
                          }
                          Square : -List~string~ messages
                          Square : +setMessages(List~string~ messages)
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("class");
    }

    // === State diagram edge cases ===

    [Fact]
    public void Parse_StateWithCompositeStates_ParsesCorrectly()
    {
        var mermaid = """
                      stateDiagram-v2
                          [*] --> Active
                          state Active {
                              [*] --> Idle
                              Idle --> Processing : start
                              Processing --> Idle : done
                          }
                          Active --> [*]
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("state");
    }

    [Fact]
    public void Parse_StateWithForkJoin_ParsesCorrectly()
    {
        var mermaid = """
                      stateDiagram-v2
                          state fork_state <<fork>>
                          [*] --> fork_state
                          fork_state --> State2
                          fork_state --> State3
                          state join_state <<join>>
                          State2 --> join_state
                          State3 --> join_state
                          join_state --> State4
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("state");
    }

    // === ER diagram edge cases ===

    [Fact]
    public void Parse_ErWithAllRelationshipTypes_ParsesCorrectly()
    {
        var mermaid = """
                      erDiagram
                          CUSTOMER ||--o{ ORDER : places
                          ORDER ||--|{ LINE-ITEM : contains
                          CUSTOMER }|..|{ DELIVERY-ADDRESS : uses
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("er");
    }

    // === Diagram types that regex handles generically ===

    [Fact]
    public void Parse_Journey_DetectsType()
    {
        var mermaid = """
                      journey
                          title My working day
                          section Go to work
                              Make tea: 5: Me
                              Go upstairs: 3: Me
                              Do work: 1: Me, Cat
                          section Go home
                              Go downstairs: 5: Me
                              Sit down: 5: Me
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("journey");
    }

    [Fact]
    public void Parse_Timeline_DetectsType()
    {
        var mermaid = """
                      timeline
                          title History of Social Media
                          2002 : LinkedIn
                          2004 : Facebook : Google
                          2005 : YouTube
                          2006 : Twitter
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("timeline");
    }

    [Fact]
    public void Parse_QuadrantChart_DetectsType()
    {
        var mermaid = """
                      quadrantChart
                          title Reach and engagement
                          x-axis Low Reach --> High Reach
                          y-axis Low Engagement --> High Engagement
                          quadrant-1 We should expand
                          quadrant-2 Need to promote
                          quadrant-3 Re-evaluate
                          quadrant-4 May be improved
                          Campaign A: [0.3, 0.6]
                          Campaign B: [0.45, 0.23]
                      """;

        var result = _jint.Parse(mermaid);
        result.DiagramType.Should().Be("quadrant");
    }

    // === Jint vs Regex parity: both should detect the same diagram type ===

    [Theory]
    [InlineData("graph TD\n    A --> B", "flowchart")]
    [InlineData("flowchart LR\n    A --> B", "flowchart")]
    [InlineData("sequenceDiagram\n    Alice->>Bob: Hi", "sequence")]
    [InlineData("classDiagram\n    class Animal", "class")]
    [InlineData("stateDiagram\n    [*] --> Idle", "state")]
    [InlineData("erDiagram\n    A ||--o{ B : has", "er")]
    [InlineData("gantt\n    title Plan\n    section A\n    Task : a1, 2024-01-01, 7d", "gantt")]
    [InlineData("pie\n    \"A\" : 50\n    \"B\" : 50", "pie")]
    public void Parse_DiagramTypeParity_JintMatchesRegex(string mermaid, string expectedType)
    {
        var jintResult = _jint.Parse(mermaid);
        var regexResult = _regex.Parse(mermaid);

        jintResult.DiagramType.Should().Be(expectedType);
        regexResult.DiagramType.Should().Be(expectedType);
    }
}
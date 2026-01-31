using System.Text;
using System.Text.RegularExpressions;
using CodeSummarizer.Models;

namespace CodeSummarizer.Parsing;

/// <summary>
///     Regex-based parser for Mermaid diagram syntax.
///     Extracts diagram type, nodes, edges, and labels for summarization.
///     Mermaid syntax is regular enough that a full parser isn't needed —
///     we just need enough structure to describe the diagram shape.
/// </summary>
public static partial class MermaidParser
{
    /// <summary>
    ///     Parse mermaid code and return a structural summary.
    /// </summary>
    public static MermaidSummary Parse(string mermaidCode)
    {
        if (string.IsNullOrWhiteSpace(mermaidCode))
            return new MermaidSummary
            {
                Description = "Empty diagram",
                DiagramType = "unknown",
                Source = "parser",
                OriginalCode = mermaidCode ?? ""
            };

        var trimmed = mermaidCode.Trim();
        var firstLine = trimmed.Split('\n')[0].Trim();

        // Detect diagram type from first line
        var (diagramType, direction) = DetectDiagramType(firstLine);

        return diagramType switch
        {
            "flowchart" => ParseFlowchart(trimmed, direction),
            "sequence" => ParseSequenceDiagram(trimmed),
            "class" => ParseClassDiagram(trimmed),
            "state" => ParseStateDiagram(trimmed),
            "er" => ParseErDiagram(trimmed),
            "gantt" => ParseGantt(trimmed),
            "pie" => ParsePie(trimmed),
            _ => ParseGeneric(trimmed, diagramType)
        };
    }

    private static (string type, string direction) DetectDiagramType(string firstLine)
    {
        var lower = firstLine.ToLowerInvariant();

        if (FlowchartRegex().IsMatch(firstLine))
        {
            var match = FlowchartRegex().Match(firstLine);
            var dir = match.Groups[2].Value;
            return ("flowchart", dir.Length > 0 ? dir : "TD");
        }

        if (lower.StartsWith("sequencediagram")) return ("sequence", "");
        if (lower.StartsWith("classdiagram")) return ("class", "");
        if (lower.StartsWith("statediagram")) return ("state", "");
        if (lower.StartsWith("erdiagram")) return ("er", "");
        if (lower.StartsWith("gantt")) return ("gantt", "");
        if (lower.StartsWith("pie")) return ("pie", "");
        if (lower.StartsWith("journey")) return ("journey", "");
        if (lower.StartsWith("gitgraph")) return ("gitgraph", "");
        if (lower.StartsWith("mindmap")) return ("mindmap", "");
        if (lower.StartsWith("timeline")) return ("timeline", "");
        if (lower.StartsWith("quadrantchart")) return ("quadrant", "");
        if (lower.StartsWith("xychart")) return ("xychart", "");
        if (lower.StartsWith("block")) return ("block", "");
        if (lower.StartsWith("sankey")) return ("sankey", "");

        return ("unknown", "");
    }

    private static MermaidSummary ParseFlowchart(string code, string direction)
    {
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodeLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<string>();

        foreach (var line in GetContentLines(code))
        {
            // Match node definitions: A[label], B{label}, C((label)), D(label), E([label])
            foreach (Match m in NodeDefinitionRegex().Matches(line))
            {
                var id = m.Groups[1].Value;
                var label = m.Groups[2].Value;
                nodes.Add(id);
                if (!string.IsNullOrWhiteSpace(label))
                    nodeLabels[id] = label.Trim();
            }

            // Match edges: A --> B, A -->|text| B, A -.-> B, A ==> B
            foreach (Match m in FlowchartEdgeRegex().Matches(line))
            {
                var from = m.Groups[1].Value.Trim();
                var edgeLabel = m.Groups[3].Value.Trim();
                var to = m.Groups[4].Value.Trim();
                // strip node shape chars from to
                to = StripNodeShape(to);
                nodes.Add(from);
                nodes.Add(to);

                var label = edgeLabel.Length > 0 ? $" ({edgeLabel})" : "";
                edges.Add($"{GetLabel(from, nodeLabels)} -> {GetLabel(to, nodeLabels)}{label}");
            }

            // Match subgraph
            var subMatch = SubgraphRegex().Match(line);
            if (subMatch.Success)
            {
                var name = subMatch.Groups[1].Value.Trim();
                if (name.Length > 0)
                    nodes.Add($"[subgraph: {name}]");
            }
        }

        var dirDesc = direction.ToUpperInvariant() switch
        {
            "TD" or "TB" => "top-down",
            "BT" => "bottom-up",
            "LR" => "left-right",
            "RL" => "right-left",
            _ => direction
        };

        var desc = BuildDescription("Flowchart", dirDesc, nodes, edges, nodeLabels);

        return new MermaidSummary
        {
            Description = desc,
            DiagramType = "flowchart",
            Nodes = nodes.ToList(),
            Edges = edges,
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParseSequenceDiagram(string code)
    {
        var participants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();

        foreach (var line in GetContentLines(code))
        {
            // participant A as Alias
            var partMatch = ParticipantRegex().Match(line);
            if (partMatch.Success)
            {
                var alias = partMatch.Groups[2].Value;
                participants.Add(alias.Length > 0 ? alias : partMatch.Groups[1].Value);
                continue;
            }

            // A->>B: message, A-->>B: message, A->>+B: message
            var msgMatch = SequenceMessageRegex().Match(line);
            if (msgMatch.Success)
            {
                var from = msgMatch.Groups[1].Value.Trim();
                // Group 2 is the arrow (->>), skip it
                var to = msgMatch.Groups[3].Value.Trim();
                var msg = msgMatch.Groups[4].Value.Trim();
                participants.Add(from);
                participants.Add(to);
                messages.Add($"{from} -> {to}: {msg}");
            }
        }

        var sb = new StringBuilder();
        sb.Append($"Sequence diagram with {participants.Count} participants");
        if (participants.Count <= 6)
            sb.Append($" ({string.Join(", ", participants)})");
        sb.Append($" and {messages.Count} messages.");

        if (messages.Count > 0 && messages.Count <= 5)
        {
            sb.Append(' ');
            sb.Append(string.Join("; ", messages));
        }

        return new MermaidSummary
        {
            Description = sb.ToString(),
            DiagramType = "sequence",
            Nodes = participants.ToList(),
            Edges = messages,
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParseClassDiagram(string code)
    {
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relationships = new List<string>();

        foreach (var line in GetContentLines(code))
        {
            // class ClassName
            var classMatch = ClassDefRegex().Match(line);
            if (classMatch.Success)
            {
                classes.Add(classMatch.Groups[1].Value);
                continue;
            }

            // ClassA <|-- ClassB, ClassA *-- ClassB, etc.
            var relMatch = ClassRelationRegex().Match(line);
            if (relMatch.Success)
            {
                var left = relMatch.Groups[1].Value.Trim();
                var rel = relMatch.Groups[2].Value.Trim();
                var right = relMatch.Groups[3].Value.Trim();
                classes.Add(left);
                classes.Add(right);

                var relType = rel switch
                {
                    "<|--" or "--|>" => "inherits",
                    "*--" or "--*" => "composes",
                    "o--" or "--o" => "aggregates",
                    "<..>" or "..>" or "<.." => "depends",
                    "--" => "associates",
                    _ => "relates"
                };
                relationships.Add($"{left} {relType} {right}");
            }
        }

        var sb = new StringBuilder();
        sb.Append($"Class diagram with {classes.Count} classes");
        if (classes.Count <= 8)
            sb.Append($" ({string.Join(", ", classes)})");
        if (relationships.Count > 0)
        {
            sb.Append($" and {relationships.Count} relationships");
            if (relationships.Count <= 5)
            {
                sb.Append(": ");
                sb.Append(string.Join("; ", relationships));
            }
        }

        sb.Append('.');

        return new MermaidSummary
        {
            Description = sb.ToString(),
            DiagramType = "class",
            Nodes = classes.ToList(),
            Edges = relationships,
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParseStateDiagram(string code)
    {
        var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transitions = new List<string>();

        foreach (var line in GetContentLines(code))
        {
            // state1 --> state2 : label
            var match = StateTransitionRegex().Match(line);
            if (match.Success)
            {
                var from = match.Groups[1].Value.Trim();
                var to = match.Groups[2].Value.Trim();
                var label = match.Groups[3].Value.Trim();

                if (from != "[*]") states.Add(from);
                if (to != "[*]") states.Add(to);

                var labelPart = label.Length > 0 ? $" ({label})" : "";
                transitions.Add($"{from} -> {to}{labelPart}");
            }
        }

        var sb = new StringBuilder();
        sb.Append($"State diagram with {states.Count} states");
        if (states.Count <= 8)
            sb.Append($" ({string.Join(", ", states)})");
        sb.Append($" and {transitions.Count} transitions.");

        return new MermaidSummary
        {
            Description = sb.ToString(),
            DiagramType = "state",
            Nodes = states.ToList(),
            Edges = transitions,
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParseErDiagram(string code)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relationships = new List<string>();

        foreach (var line in GetContentLines(code))
        {
            // ENTITY1 ||--o{ ENTITY2 : "relationship"
            var match = ErRelationRegex().Match(line);
            if (match.Success)
            {
                var left = match.Groups[1].Value.Trim();
                var right = match.Groups[2].Value.Trim();
                var label = match.Groups[3].Value.Trim().Trim('"');
                entities.Add(left);
                entities.Add(right);
                relationships.Add($"{left} {label} {right}");
            }
        }

        var sb = new StringBuilder();
        sb.Append($"ER diagram with {entities.Count} entities");
        if (entities.Count <= 8)
            sb.Append($" ({string.Join(", ", entities)})");
        if (relationships.Count > 0)
            sb.Append($" and {relationships.Count} relationships");
        sb.Append('.');

        return new MermaidSummary
        {
            Description = sb.ToString(),
            DiagramType = "er",
            Nodes = entities.ToList(),
            Edges = relationships,
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParseGantt(string code)
    {
        string? title = null;
        var sections = new List<string>();
        var tasks = new List<string>();

        foreach (var line in GetContentLines(code))
        {
            if (line.TrimStart().StartsWith("title", StringComparison.OrdinalIgnoreCase))
            {
                title = line.TrimStart()[5..].Trim().TrimStart(':').Trim();
                continue;
            }

            if (line.TrimStart().StartsWith("section", StringComparison.OrdinalIgnoreCase))
            {
                sections.Add(line.TrimStart()[7..].Trim());
                continue;
            }

            // Task lines: "Task name : status, id, start, duration"
            var taskMatch = GanttTaskRegex().Match(line);
            if (taskMatch.Success)
                tasks.Add(taskMatch.Groups[1].Value.Trim());
        }

        var sb = new StringBuilder();
        sb.Append("Gantt chart");
        if (title != null) sb.Append($": {title}");
        if (sections.Count > 0) sb.Append($". {sections.Count} sections ({string.Join(", ", sections)})");
        sb.Append($", {tasks.Count} tasks.");

        return new MermaidSummary
        {
            Description = sb.ToString(),
            DiagramType = "gantt",
            Nodes = sections.Concat(tasks).ToList(),
            Edges = [],
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParsePie(string code)
    {
        string? title = null;
        var slices = new List<string>();

        // Check if title is on the first line (e.g., "pie title My Chart")
        var firstLine = code.Trim().Split('\n')[0].Trim();
        var pieTitleMatch = System.Text.RegularExpressions.Regex.Match(
            firstLine, @"^pie\s+title\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pieTitleMatch.Success)
            title = pieTitleMatch.Groups[1].Value.Trim();

        foreach (var line in GetContentLines(code))
        {
            if (line.TrimStart().StartsWith("title", StringComparison.OrdinalIgnoreCase))
            {
                title = line.TrimStart()[5..].Trim();
                continue;
            }

            // "Label" : value
            var sliceMatch = PieSliceRegex().Match(line);
            if (sliceMatch.Success)
            {
                var label = sliceMatch.Groups[1].Value;
                var value = sliceMatch.Groups[2].Value;
                slices.Add($"{label} ({value})");
            }
        }

        var sb = new StringBuilder();
        sb.Append("Pie chart");
        if (title != null) sb.Append($": {title}");
        sb.Append($" with {slices.Count} slices");
        if (slices.Count <= 8)
            sb.Append($" — {string.Join(", ", slices)}");
        sb.Append('.');

        return new MermaidSummary
        {
            Description = sb.ToString(),
            DiagramType = "pie",
            Nodes = slices,
            Edges = [],
            Source = "parser",
            OriginalCode = code
        };
    }

    private static MermaidSummary ParseGeneric(string code, string diagramType)
    {
        var lines = code.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var contentLines = lines.Skip(1).Count(); // skip header line

        return new MermaidSummary
        {
            Description = $"{diagramType} diagram with {contentLines} definition lines.",
            DiagramType = diagramType,
            Nodes = [],
            Edges = [],
            Source = "parser",
            OriginalCode = code
        };
    }

    // --- Helpers ---

    private static IEnumerable<string> GetContentLines(string code)
    {
        return code.Split('\n')
            .Skip(1) // skip the diagram type line
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim());
    }

    private static string GetLabel(string id, Dictionary<string, string> labels)
    {
        return labels.TryGetValue(id, out var label) ? label : id;
    }

    private static string StripNodeShape(string nodeRef)
    {
        // Strip trailing shape chars: A[label] -> A, B{label} -> B, C(label) -> C
        var match = NodeIdFromRefRegex().Match(nodeRef);
        return match.Success ? match.Groups[1].Value : nodeRef;
    }

    private static string BuildDescription(
        string type, string direction,
        HashSet<string> nodes, List<string> edges,
        Dictionary<string, string> nodeLabels)
    {
        var sb = new StringBuilder();
        sb.Append($"{type} ({direction}): {nodes.Count} nodes, {edges.Count} edges.");

        // Show the flow for small diagrams
        if (edges.Count > 0 && edges.Count <= 6)
        {
            sb.Append(' ');
            sb.Append(string.Join("; ", edges));
        }

        return sb.ToString();
    }

    // --- Generated Regexes ---

    [GeneratedRegex(@"^(flowchart|graph)\s+(TD|TB|BT|LR|RL)", RegexOptions.IgnoreCase)]
    private static partial Regex FlowchartRegex();

    [GeneratedRegex(@"(\w+)[\[\({]([^\]\)}]+)[\]\)}]")]
    private static partial Regex NodeDefinitionRegex();

    [GeneratedRegex(@"(\w+)\s*(-->|-.->|==>|---)(\|[^|]*\|)?\s*(\S+)")]
    private static partial Regex FlowchartEdgeRegex();

    [GeneratedRegex(@"^subgraph\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubgraphRegex();

    [GeneratedRegex(@"^participant\s+(\S+)(?:\s+as\s+(.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ParticipantRegex();

    // Mermaid sequence diagram arrows per spec:
    // ->, -->, ->>, -->>, <<->>, <<-->>, -x, --x, -), --)
    // Plus optional +/- suffixes for activation/deactivation
    [GeneratedRegex(@"^(\w+)\s*(<<--?>>|--?>>|--?>|--?x|--?\))\+?-?\s*(\w+)\s*:\s*(.+)$")]
    private static partial Regex SequenceMessageRegex();

    [GeneratedRegex(@"^\s*class\s+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClassDefRegex();

    [GeneratedRegex(@"^(\w+)\s+(<\|--|--\|>|\*--|\*-|o--|--o|<\.\.>|\.\.\>|<\.\.|--)\s+(\w+)")]
    private static partial Regex ClassRelationRegex();

    [GeneratedRegex(@"^(\[?\*?\]?|\w+)\s*-->\s*(\[?\*?\]?|\w+)\s*(?::\s*(.+))?$")]
    private static partial Regex StateTransitionRegex();

    [GeneratedRegex(@"^(\w+)\s+[\|o\{][\|o\{]--[\|o\}][\|o\}]\s+(\w+)\s*:\s*(.+)$")]
    private static partial Regex ErRelationRegex();

    [GeneratedRegex(@"^([^:]+?)\s*:\s*(.+)$")]
    private static partial Regex GanttTaskRegex();

    [GeneratedRegex(@"""([^""]+)""\s*:\s*([\d.]+)")]
    private static partial Regex PieSliceRegex();

    [GeneratedRegex(@"^(\w+)[\[\(\{]")]
    private static partial Regex NodeIdFromRefRegex();
}

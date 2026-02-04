using CodeSummarizer.Models;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;

namespace CodeSummarizer.Mermaid.Jint;

/// <summary>
///     Maps Jint JsValue results from mermaid.js parse → <see cref="MermaidSummary" />.
/// </summary>
internal static class MermaidDataExtractor
{
    /// <summary>
    ///     Extract a MermaidSummary from the JS parse result.
    ///     Expected shape: { diagramType: string, nodes: string[], edges: string[], description: string }
    /// </summary>
    public static MermaidSummary Extract(JsValue result, string originalCode)
    {
        if (result is not ObjectInstance obj)
            return new MermaidSummary
            {
                Description = "Parsed by mermaid.js (no structured output)",
                DiagramType = DetectDiagramTypeFromCode(originalCode),
                Source = "jint",
                OriginalCode = originalCode
            };

        var diagramType = GetString(obj, "diagramType") ?? DetectDiagramTypeFromCode(originalCode);
        var description = GetString(obj, "description") ?? $"Mermaid {diagramType} diagram";
        var nodes = GetStringArray(obj, "nodes");
        var edges = GetStringArray(obj, "edges");

        return new MermaidSummary
        {
            Description = description,
            DiagramType = diagramType,
            Nodes = nodes,
            Edges = edges,
            Source = "jint",
            OriginalCode = originalCode
        };
    }

    /// <summary>
    ///     Create a simple success summary when mermaid.parse() succeeds but
    ///     doesn't return structured data (just validates syntax).
    /// </summary>
    public static MermaidSummary CreateValidationOnlySummary(string originalCode)
    {
        var diagramType = DetectDiagramTypeFromCode(originalCode);
        return new MermaidSummary
        {
            Description = $"Valid mermaid {diagramType} diagram (syntax verified by mermaid.js)",
            DiagramType = diagramType,
            Source = "jint",
            OriginalCode = originalCode
        };
    }

    private static string? GetString(ObjectInstance obj, string property)
    {
        var val = obj.Get(property);
        if (val is JsString jsStr)
            return jsStr.ToString();
        if (val.Type is Types.Undefined or Types.Null)
            return null;
        return val.ToString();
    }

    private static List<string> GetStringArray(ObjectInstance obj, string property)
    {
        var val = obj.Get(property);
        if (val is not ArrayInstance arr)
            return [];

        var result = new List<string>();
        // Use the "length" property via JS semantics
        var lengthVal = arr.Get("length");
        if (!uint.TryParse(lengthVal.ToString(), out var length))
            return result;

        for (uint i = 0; i < length; i++)
        {
            var item = arr.Get(i.ToString());
            if (item.Type is not (Types.Undefined or Types.Null))
                result.Add(item.ToString());
        }

        return result;
    }

    private static string DetectDiagramTypeFromCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "unknown";

        var firstLine = code.Trim().Split('\n')[0].Trim().ToLowerInvariant();

        if (firstLine.StartsWith("graph") || firstLine.StartsWith("flowchart")) return "flowchart";
        if (firstLine.StartsWith("sequencediagram")) return "sequence";
        if (firstLine.StartsWith("classdiagram")) return "class";
        if (firstLine.StartsWith("statediagram")) return "state";
        if (firstLine.StartsWith("erdiagram")) return "er";
        if (firstLine.StartsWith("gantt")) return "gantt";
        if (firstLine.StartsWith("pie")) return "pie";
        if (firstLine.StartsWith("journey")) return "journey";
        if (firstLine.StartsWith("gitgraph")) return "gitgraph";
        if (firstLine.StartsWith("mindmap")) return "mindmap";
        if (firstLine.StartsWith("timeline")) return "timeline";
        if (firstLine.StartsWith("quadrantchart")) return "quadrant";
        if (firstLine.StartsWith("xychart")) return "xychart";
        if (firstLine.StartsWith("block")) return "block";
        if (firstLine.StartsWith("sankey")) return "sankey";

        return "unknown";
    }
}
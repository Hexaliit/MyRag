using System.Text;
using CodeSummarizer.Models;

namespace CodeSummarizer.Services;

/// <summary>
///     Builds LLM prompts from structural code info for semantic summarization.
///     Used by CodeSummarizerService when an LLM is available for richer descriptions.
/// </summary>
public static class CodePromptBuilder
{
    /// <summary>
    ///     Build a prompt for code summarization given structural info from tree-sitter.
    /// </summary>
    public static string BuildCodePrompt(string code, string language, CodeStructure structure)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Describe what this code does in ONE sentence. Be specific about the purpose, not the syntax.");
        sb.AppendLine();
        sb.AppendLine($"Language: {language}");

        if (structure.Parsed)
        {
            if (structure.Functions.Count > 0)
                sb.AppendLine($"Functions: {string.Join(", ", structure.Functions.Select(f => f.Name))}");

            if (structure.Classes.Count > 0)
                sb.AppendLine($"Types: {string.Join(", ", structure.Classes.Select(c => c.ToString()))}");

            if (structure.Imports.Count > 0)
            {
                var topImports = structure.Imports.Take(5);
                sb.AppendLine($"Imports: {string.Join(", ", topImports)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Code:");

        // Truncate code to fit reasonable LLM context
        var codeToSend = code.Length > 2000 ? code[..2000] + "\n// ... truncated" : code;
        sb.AppendLine(codeToSend);

        return sb.ToString();
    }

    /// <summary>
    ///     Build a prompt for mermaid diagram summarization.
    /// </summary>
    public static string BuildMermaidPrompt(string mermaidCode, MermaidSummary parsedSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "Describe what this diagram shows in ONE sentence. Focus on the system/process being modeled, not the diagram syntax.");
        sb.AppendLine();
        sb.AppendLine($"Diagram type: {parsedSummary.DiagramType}");

        if (parsedSummary.Nodes.Count > 0)
            sb.AppendLine($"Components: {string.Join(", ", parsedSummary.Nodes.Take(10))}");

        if (parsedSummary.Edges.Count > 0)
            sb.AppendLine($"Relationships: {string.Join("; ", parsedSummary.Edges.Take(5))}");

        sb.AppendLine();
        sb.AppendLine("Mermaid code:");
        sb.AppendLine(mermaidCode.Length > 1500 ? mermaidCode[..1500] + "\n%% ... truncated" : mermaidCode);

        return sb.ToString();
    }

    /// <summary>
    ///     Build a structural-only description (no LLM needed).
    ///     Used as fallback when no LLM is available.
    /// </summary>
    public static string BuildStructuralDescription(string language, CodeStructure structure)
    {
        var parts = new List<string>();

        if (structure.Classes.Count > 0)
        {
            var classNames = string.Join(", ", structure.Classes.Select(c => c.ToString()));
            parts.Add($"defines {classNames}");
        }

        if (structure.Functions.Count > 0)
        {
            if (structure.Functions.Count <= 4)
            {
                var funcNames = string.Join(", ", structure.Functions.Select(f => f.ToString()));
                parts.Add($"with functions {funcNames}");
            }
            else
            {
                var first3 = string.Join(", ", structure.Functions.Take(3).Select(f => f.Name));
                parts.Add($"with {structure.Functions.Count} functions including {first3}");
            }
        }

        if (structure.Imports.Count > 0) parts.Add($"importing {structure.Imports.Count} dependencies");

        if (parts.Count == 0)
            return $"{language} code ({structure.TotalLines} lines)";

        return $"{language} code that {string.Join(", ", parts)} ({structure.TotalLines} lines)";
    }
}
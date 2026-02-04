using CodeSummarizer.Models;
using CodeSummarizer.Parsing;
using Microsoft.Extensions.Logging;

namespace CodeSummarizer.Services;

/// <summary>
///     Orchestrates code summarization: Tree-sitter parsing → optional LLM → description.
///     Designed to be injected as a singleton service in the DI container.
/// </summary>
public sealed class CodeSummarizerService : ICodeSummarizer, IDisposable
{
    private readonly ILogger<CodeSummarizerService> _logger;
    private readonly IMermaidParser _mermaidParser;
    private readonly TreeSitterParser _treeSitterParser;

    public CodeSummarizerService(ILogger<CodeSummarizerService> logger, IMermaidParser? mermaidParser = null)
    {
        _logger = logger;
        _mermaidParser = mermaidParser ?? new RegexMermaidParser();
        _treeSitterParser = new TreeSitterParser();
    }

    /// <summary>
    ///     Optional delegate for LLM sentinel calls.
    ///     Matches the signature of OllamaService.SentinelGenerateAsync.
    ///     When null, only structural summaries are produced.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string?>>? SentinelGenerate { get; set; }

    /// <summary>
    ///     Summarize a code block using tree-sitter AST parsing + optional LLM.
    /// </summary>
    public async Task<CodeSummary> SummarizeCodeAsync(
        string code, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return EmptyCodeSummary(language, code);

        var normalizedLang = language.Trim().ToLowerInvariant();

        // 1. Structural parsing via tree-sitter (or heuristic fallback)
        var structure = _treeSitterParser.Parse(code, normalizedLang);

        // 2. Build structural description
        var structuralDesc = CodePromptBuilder.BuildStructuralDescription(normalizedLang, structure);

        // 3. Try LLM for semantic description if available
        if (SentinelGenerate != null)
            try
            {
                var prompt = CodePromptBuilder.BuildCodePrompt(code, normalizedLang, structure);
                var llmDesc = await SentinelGenerate("Describe this code in one sentence.", prompt, ct);

                if (!string.IsNullOrWhiteSpace(llmDesc))
                    return new CodeSummary
                    {
                        Description = llmDesc.Trim(),
                        Language = normalizedLang,
                        Functions = structure.Functions.Select(f => f.ToString()).ToList(),
                        Classes = structure.Classes.Select(c => c.ToString()).ToList(),
                        Imports = structure.Imports,
                        Source = "llm",
                        OriginalCode = code
                    };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LLM summarization failed, using structural fallback");
            }

        // 4. Structural-only summary (tree-sitter or heuristic)
        var source = structure.Parsed ? "tree-sitter" : "heuristic";
        return new CodeSummary
        {
            Description = structuralDesc,
            Language = normalizedLang,
            Functions = structure.Functions.Select(f => f.ToString()).ToList(),
            Classes = structure.Classes.Select(c => c.ToString()).ToList(),
            Imports = structure.Imports,
            Source = source,
            OriginalCode = code
        };
    }

    /// <summary>
    ///     Summarize a Mermaid diagram using regex parser + optional LLM.
    /// </summary>
    public async Task<MermaidSummary> SummarizeMermaidAsync(
        string mermaidCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mermaidCode))
            return new MermaidSummary
            {
                Description = "Empty diagram",
                DiagramType = "unknown",
                Source = "parser",
                OriginalCode = mermaidCode ?? ""
            };

        // 1. Parse with injected mermaid parser (regex by default, Jint if registered)
        var parsed = _mermaidParser.Parse(mermaidCode);

        // 2. Try LLM for semantic description if available
        if (SentinelGenerate != null)
            try
            {
                var prompt = CodePromptBuilder.BuildMermaidPrompt(mermaidCode, parsed);
                var llmDesc = await SentinelGenerate("Describe this diagram in one sentence.", prompt, ct);

                if (!string.IsNullOrWhiteSpace(llmDesc))
                    return parsed with
                    {
                        Description = llmDesc.Trim(),
                        Source = "llm"
                    };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LLM diagram summarization failed, using parser fallback");
            }

        return parsed;
    }

    /// <summary>
    ///     Check if a language is supported for AST parsing.
    /// </summary>
    public bool IsLanguageSupported(string language)
    {
        return LanguageRegistry.IsSupported(language);
    }

    public void Dispose()
    {
        _treeSitterParser.Dispose();
    }

    /// <summary>
    ///     Synchronous code summary (for contexts where async isn't possible).
    ///     Only produces structural summaries (no LLM call).
    /// </summary>
    public CodeSummary SummarizeCodeSync(string code, string language)
    {
        if (string.IsNullOrWhiteSpace(code))
            return EmptyCodeSummary(language, code);

        var normalizedLang = language.Trim().ToLowerInvariant();
        var structure = _treeSitterParser.Parse(code, normalizedLang);
        var desc = CodePromptBuilder.BuildStructuralDescription(normalizedLang, structure);
        var source = structure.Parsed ? "tree-sitter" : "heuristic";

        return new CodeSummary
        {
            Description = desc,
            Language = normalizedLang,
            Functions = structure.Functions.Select(f => f.ToString()).ToList(),
            Classes = structure.Classes.Select(c => c.ToString()).ToList(),
            Imports = structure.Imports,
            Source = source,
            OriginalCode = code
        };
    }

    private static CodeSummary EmptyCodeSummary(string language, string code)
    {
        return new CodeSummary
        {
            Description = $"Empty {language} code block",
            Language = language,
            Source = "heuristic",
            OriginalCode = code ?? ""
        };
    }
}
using System.Text;
using System.Text.RegularExpressions;
using LucidRAG.UltraResearch.Signals;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Extracts rich evidence artifacts (code blocks, tables, equations) from ingested paper markdown.
///     Runs after IngestWave — parses the saved markdown file to create typed evidence records.
///     Fast lane — CPU-only text parsing, no ML or IO.
/// </summary>
public sealed partial class PaperEvidenceExtractor : ITypedAnalysisWave<FetchCandidate>
{
    private readonly ILogger<PaperEvidenceExtractor> _logger;

    public PaperEvidenceExtractor(ILogger<PaperEvidenceExtractor> logger)
    {
        _logger = logger;
    }

    public string Name => "research.evidence_extract";
    public string Description => "Extract code blocks, tables, and equations from paper markdown";
    public int Priority => 35; // After IngestWave (30), before AuthorPromotion (40)
    public IReadOnlyList<string> Tags => ["fast", SignalTags.Content];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        // Only run if ingest succeeded and we have the markdown file
        var ingestSuccess = context.GetValue<bool>(ResearchSignalKeys.PaperIngestSuccess);
        return ingestSuccess && context.HasSignal(ResearchSignalKeys.PaperFilePath);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        var filePath = context.GetValue<string>(ResearchSignalKeys.PaperFilePath);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return signals;

        string markdown;
        try
        {
            markdown = await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read paper file: {FilePath}", filePath);
            return signals;
        }

        // Strip YAML frontmatter
        var content = StripFrontmatter(markdown);

        // Extract evidence types
        var codeBlocks = ExtractCodeBlocks(content);
        var tables = ExtractTables(content);
        var equations = ExtractEquations(content);

        // Cache extracted evidence for downstream waves and Q&A
        var evidence = new ExtractedEvidence
        {
            CodeBlocks = codeBlocks,
            Tables = tables,
            Equations = equations
        };
        context.SetCached("paper_evidence", evidence);

        // Emit signals
        if (codeBlocks.Count > 0)
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperEvidenceCodeBlocks,
                Value = codeBlocks.Count,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Content, "evidence"],
                Metadata = new Dictionary<string, object>
                {
                    ["languages"] = codeBlocks
                        .Select(c => c.Language)
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Distinct()
                        .ToList()
                }
            });
        }

        if (tables.Count > 0)
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperEvidenceTables,
                Value = tables.Count,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Content, "evidence"]
            });
        }

        if (equations.Count > 0)
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperEvidenceEquations,
                Value = equations.Count,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Content, "evidence"]
            });
        }

        var total = codeBlocks.Count + tables.Count + equations.Count;
        if (total > 0)
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperEvidenceTotal,
                Value = total,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Content, "evidence"]
            });

            _logger.LogInformation(
                "Evidence extracted from {PaperId}: {Code} code blocks, {Tables} tables, {Equations} equations",
                candidate.Id, codeBlocks.Count, tables.Count, equations.Count);
        }

        return signals;
    }

    // === Extraction methods ===

    private static List<CodeBlock> ExtractCodeBlocks(string markdown)
    {
        var blocks = new List<CodeBlock>();
        var matches = CodeBlockRegex().Matches(markdown);

        foreach (Match match in matches)
        {
            var language = match.Groups["lang"].Value.Trim();
            var code = match.Groups["code"].Value;

            // Skip very short or empty code blocks
            if (string.IsNullOrWhiteSpace(code) || code.Trim().Length < 10)
                continue;

            blocks.Add(new CodeBlock
            {
                Language = language,
                Content = code.Trim(),
                StartOffset = match.Index,
                Length = match.Length
            });
        }

        return blocks;
    }

    private static List<ExtractedTable> ExtractTables(string markdown)
    {
        var tables = new List<ExtractedTable>();
        var matches = MarkdownTableRegex().Matches(markdown);

        foreach (Match match in matches)
        {
            var tableText = match.Value.Trim();
            var rows = tableText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Need at least header + separator + 1 data row
            if (rows.Length < 3) continue;

            // Parse header
            var headers = ParseTableRow(rows[0]);
            if (headers.Count == 0) continue;

            // Skip separator row (row 1), parse data rows
            var dataRows = new List<List<string>>();
            for (var i = 2; i < rows.Length; i++)
            {
                var cells = ParseTableRow(rows[i]);
                if (cells.Count > 0)
                    dataRows.Add(cells);
            }

            if (dataRows.Count == 0) continue;

            // Convert to CSV
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
            foreach (var row in dataRows)
                csv.AppendLine(string.Join(",", row.Select(EscapeCsv)));

            tables.Add(new ExtractedTable
            {
                Headers = headers,
                RowCount = dataRows.Count,
                ColumnCount = headers.Count,
                CsvContent = csv.ToString(),
                StartOffset = match.Index,
                Length = match.Length
            });
        }

        return tables;
    }

    private static List<ExtractedEquation> ExtractEquations(string markdown)
    {
        var equations = new List<ExtractedEquation>();

        // Display equations: $$...$$ (multiline)
        foreach (Match match in DisplayEquationRegex().Matches(markdown))
        {
            var latex = match.Groups["eq"].Value.Trim();
            if (string.IsNullOrWhiteSpace(latex) || latex.Length < 3) continue;

            equations.Add(new ExtractedEquation
            {
                Latex = latex,
                IsDisplay = true,
                StartOffset = match.Index,
                Length = match.Length
            });
        }

        // \begin{equation}...\end{equation} and similar environments
        foreach (Match match in LatexEnvironmentRegex().Matches(markdown))
        {
            var latex = match.Groups["eq"].Value.Trim();
            var env = match.Groups["env"].Value;
            if (string.IsNullOrWhiteSpace(latex) || latex.Length < 3) continue;

            // Avoid duplicates with $$ detection
            var alreadyCaptured = equations.Any(e =>
                e.StartOffset <= match.Index && e.StartOffset + e.Length >= match.Index + match.Length);
            if (alreadyCaptured) continue;

            equations.Add(new ExtractedEquation
            {
                Latex = latex,
                IsDisplay = true,
                Environment = env,
                StartOffset = match.Index,
                Length = match.Length
            });
        }

        return equations;
    }

    // === Helpers ===

    private static string StripFrontmatter(string markdown)
    {
        if (!markdown.StartsWith("---")) return markdown;
        var endIndex = markdown.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (endIndex < 0) return markdown;
        return markdown[(endIndex + 5)..];
    }

    private static List<string> ParseTableRow(string row)
    {
        if (string.IsNullOrWhiteSpace(row)) return [];
        return row.Split('|', StringSplitOptions.TrimEntries)
            .Where(c => !string.IsNullOrWhiteSpace(c) && !IsSeparatorCell(c))
            .ToList();
    }

    private static bool IsSeparatorCell(string cell)
    {
        return cell.All(c => c == '-' || c == ':' || c == ' ');
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // === Compiled regexes ===

    [GeneratedRegex(@"```(?<lang>\w*)\s*\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"^\|.+\|\s*\n\|[-:\s|]+\|\s*\n(?:\|.+\|\s*\n?)+", RegexOptions.Multiline)]
    private static partial Regex MarkdownTableRegex();

    [GeneratedRegex(@"\$\$\s*(?<eq>.+?)\s*\$\$", RegexOptions.Singleline)]
    private static partial Regex DisplayEquationRegex();

    [GeneratedRegex(@"\\begin\{(?<env>equation|align|gather|multline)\*?\}(?<eq>.*?)\\end\{\k<env>\*?\}",
        RegexOptions.Singleline)]
    private static partial Regex LatexEnvironmentRegex();
}

// === Evidence models ===

/// <summary>
///     Extracted evidence from a paper, cached in AnalysisContext for downstream use.
/// </summary>
public sealed class ExtractedEvidence
{
    public List<CodeBlock> CodeBlocks { get; init; } = [];
    public List<ExtractedTable> Tables { get; init; } = [];
    public List<ExtractedEquation> Equations { get; init; } = [];

    public int TotalCount => CodeBlocks.Count + Tables.Count + Equations.Count;
}

public sealed class CodeBlock
{
    public string Language { get; init; } = "";
    public required string Content { get; init; }
    public int StartOffset { get; init; }
    public int Length { get; init; }
}

public sealed class ExtractedTable
{
    public List<string> Headers { get; init; } = [];
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public required string CsvContent { get; init; }
    public int StartOffset { get; init; }
    public int Length { get; init; }
}

public sealed class ExtractedEquation
{
    public required string Latex { get; init; }
    public bool IsDisplay { get; init; }
    public string? Environment { get; init; }
    public int StartOffset { get; init; }
    public int Length { get; init; }
}

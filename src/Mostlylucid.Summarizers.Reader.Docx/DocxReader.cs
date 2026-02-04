using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DoomSummarizer.Helpers;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizers.Reader.Docx;

/// <summary>
///     DOCX document reader using Open XML SDK.
///     Heading-aware extraction with list and table support.
/// </summary>
public class DocxReader : IDocumentReader
{
    private static readonly string[] _extensions = [".docx"];
    private readonly ILogger<DocxReader> _logger;

    public DocxReader(ILogger<DocxReader> logger)
    {
        _logger = logger;
    }

    public string ReaderName => "docx";
    public IReadOnlyList<string> SupportedExtensions => _extensions;
    public int Priority => 100;

    public Task<ReaderResult> ReadAsync(string filePath, ReaderOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ReaderOptions();
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();
        string? title = null;

        var sb = new StringBuilder();

        using (var doc = WordprocessingDocument.Open(filePath, false))
        {
            // Extract metadata from core properties
            if (doc.PackageProperties != null)
            {
                if (!string.IsNullOrWhiteSpace(doc.PackageProperties.Title))
                {
                    title = doc.PackageProperties.Title;
                    metadata["title"] = title;
                }

                if (!string.IsNullOrWhiteSpace(doc.PackageProperties.Creator))
                    metadata["author"] = doc.PackageProperties.Creator;
                if (!string.IsNullOrWhiteSpace(doc.PackageProperties.Subject))
                    metadata["subject"] = doc.PackageProperties.Subject;
                if (doc.PackageProperties.Created.HasValue)
                    metadata["created"] = doc.PackageProperties.Created.Value.ToString("O");
                if (doc.PackageProperties.Modified.HasValue)
                    metadata["modified"] = doc.PackageProperties.Modified.Value.ToString("O");
            }

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
            {
                warnings.Add("Document body is empty or missing");
                return Task.FromResult(new ReaderResult
                {
                    Markdown = "",
                    Title = title ?? Path.GetFileNameWithoutExtension(filePath),
                    Metadata = metadata,
                    Warnings = warnings,
                    WordCount = 0
                });
            }

            foreach (var element in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();

                if (element is Paragraph para)
                    ProcessParagraph(para, sb, options);
                else if (element is Table table && options.ExtractTables) ProcessTable(table, sb);
            }
        }

        var markdown = sb.ToString().Trim();
        var wordCount = WordCounter.Count(markdown);

        return Task.FromResult(new ReaderResult
        {
            Markdown = markdown,
            Title = title ?? Path.GetFileNameWithoutExtension(filePath),
            Metadata = metadata,
            Warnings = warnings,
            WordCount = wordCount
        });
    }

    public async Task<ReaderResult> ReadAsync(Stream stream, string fileName, ReaderOptions? options = null,
        CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"reader-docx-{Guid.NewGuid():N}.docx");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await stream.CopyToAsync(fs, ct);
            }

            return await ReadAsync(tempPath, options, ct);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static void ProcessParagraph(Paragraph para, StringBuilder sb, ReaderOptions options)
    {
        var text = para.InnerText;
        if (string.IsNullOrWhiteSpace(text)) return;

        // Detect heading style
        var style = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
        {
            var levelStr = style.Replace("Heading", "").Trim();
            if (int.TryParse(levelStr, out var level) && level is >= 1 and <= 6)
            {
                sb.Append(new string('#', level));
                sb.Append(' ');
            }
        }

        // Detect list items (numbered and bulleted)
        var numPr = para.ParagraphProperties?.NumberingProperties;
        if (numPr != null)
        {
            var level = numPr.NumberingLevelReference?.Val?.Value ?? 0;
            var indent = new string(' ', level * 2);
            sb.Append($"{indent}- ");
        }

        sb.AppendLine(text);
        sb.AppendLine();
    }

    private static void ProcessTable(Table table, StringBuilder sb)
    {
        var rows = table.Descendants<TableRow>().ToList();
        if (rows.Count == 0) return;

        sb.AppendLine();

        var isFirst = true;
        foreach (var row in rows)
        {
            var cells = row.Descendants<TableCell>()
                .Select(c => c.InnerText.Trim().Replace("|", "\\|"))
                .ToList();

            sb.AppendLine($"| {string.Join(" | ", cells)} |");

            if (isFirst)
            {
                sb.AppendLine($"| {string.Join(" | ", cells.Select(_ => "---"))} |");
                isFirst = false;
            }
        }

        sb.AppendLine();
    }
}
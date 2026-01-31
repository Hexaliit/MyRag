using System.Text;
using DoomSummarizer.Helpers;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace Mostlylucid.Summarizers.Reader.Pdf;

/// <summary>
/// PDF document reader using PdfPig.
/// Extracts text with page markers and paragraph normalization.
/// </summary>
public class PdfReader : IDocumentReader
{
    private const int DefaultMaxPages = 2000;
    private const int DefaultMaxTextChars = 5 * 1024 * 1024;

    private readonly ILogger<PdfReader> _logger;

    public PdfReader(ILogger<PdfReader> logger)
    {
        _logger = logger;
    }

    public string ReaderName => "pdf";
    private static readonly string[] _extensions = [".pdf"];
    public IReadOnlyList<string> SupportedExtensions => _extensions;
    public int Priority => 100;

    public Task<ReaderResult> ReadAsync(string filePath, ReaderOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ReaderOptions();
        var maxPages = options.MaxPages ?? DefaultMaxPages;
        var warnings = new List<string>();
        string? title = null;
        var metadata = new Dictionary<string, string>();

        using var doc = PdfDocument.Open(filePath);

        // Extract metadata
        if (doc.Information != null)
        {
            if (!string.IsNullOrWhiteSpace(doc.Information.Title))
            {
                title = doc.Information.Title;
                metadata["title"] = title;
            }
            if (!string.IsNullOrWhiteSpace(doc.Information.Author))
                metadata["author"] = doc.Information.Author;
            if (!string.IsNullOrWhiteSpace(doc.Information.Subject))
                metadata["subject"] = doc.Information.Subject;
            if (!string.IsNullOrWhiteSpace(doc.Information.Creator))
                metadata["creator"] = doc.Information.Creator;
        }
        metadata["page_count"] = doc.NumberOfPages.ToString();

        var estimatedChars = Math.Min(doc.NumberOfPages, maxPages) * 2048;
        var sb = new StringBuilder(Math.Min(estimatedChars, DefaultMaxTextChars));
        var truncated = false;
        var pageNum = 0;

        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            pageNum++;

            if (pageNum > maxPages || sb.Length > DefaultMaxTextChars)
            {
                truncated = true;
                break;
            }

            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (options.IncludePageMarkers)
                    sb.AppendLine($"<!-- PAGE:{pageNum} -->");

                sb.AppendLine(NormalizePdfText(text));
                sb.AppendLine();
            }
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine($"<!-- TRUNCATED: extracted {pageNum - 1} of {doc.NumberOfPages} pages -->");
            warnings.Add($"Truncated: extracted {pageNum - 1} of {doc.NumberOfPages} pages");
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

    public async Task<ReaderResult> ReadAsync(Stream stream, string fileName, ReaderOptions? options = null, CancellationToken ct = default)
    {
        // PdfPig requires a seekable stream or file; write to temp file
        var tempPath = Path.Combine(Path.GetTempPath(), $"reader-pdf-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            await using (var fs = File.Create(tempPath))
                await stream.CopyToAsync(fs, ct);

            return await ReadAsync(tempPath, options, ct);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Normalize PDF text by detecting and inserting paragraph breaks.
    /// </summary>
    private static string NormalizePdfText(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                result.AppendLine();
                result.AppendLine();
                continue;
            }

            result.Append(line);

            var endsWithPunctuation = line.EndsWith('.') || line.EndsWith('!') ||
                                      line.EndsWith('?') || line.EndsWith('"') ||
                                      line.EndsWith('\'');
            var isShortLine = line.Length < 60;
            var nextIsCapital = i + 1 < lines.Length &&
                                lines[i + 1].TrimStart().Length > 0 &&
                                char.IsUpper(lines[i + 1].TrimStart()[0]);

            if (endsWithPunctuation && (isShortLine || nextIsCapital))
            {
                result.AppendLine();
                result.AppendLine();
            }
            else if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
            {
                result.Append(' ');
            }
            else
            {
                result.AppendLine();
            }
        }

        return result.ToString().Trim();
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Default handler for plain text files (.txt, .md)
/// </summary>
public class TextDocumentHandler : IDocumentHandler
{
    public IReadOnlyList<string> SupportedExtensions => [".txt", ".md"];
    public int Priority => 0;
    public string HandlerName => "Text";

    public bool CanHandle(string filePath)
    {
        return File.Exists(filePath);
    }

    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        var content = await File.ReadAllTextAsync(filePath, options.CancellationToken);
        var title = Path.GetFileNameWithoutExtension(filePath);

        // For markdown, extract first header as title
        if (Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("# "))
                {
                    title = trimmed[2..].Trim();
                    break;
                }
            }
        }

        return new DocumentContent
        {
            Markdown = content,
            Title = title,
            ContentType = Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? "markdown"
                : "text"
        };
    }
}

/// <summary>
///     Default handler for HTML files
/// </summary>
public class HtmlDocumentHandler : IDocumentHandler
{
    public IReadOnlyList<string> SupportedExtensions => [".html", ".htm"];
    public int Priority => 0;
    public string HandlerName => "HTML";

    public bool CanHandle(string filePath)
    {
        return File.Exists(filePath);
    }

    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        var html = await File.ReadAllTextAsync(filePath, options.CancellationToken);

        // Extract title from HTML
        var titleMatch = Regex.Match(
            html, @"<title[^>]*>([^<]+)</title>",
            RegexOptions.IgnoreCase);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);

        // Strip HTML tags for a basic markdown conversion
        var text = StripHtml(html);

        return new DocumentContent
        {
            Markdown = text,
            Title = title,
            ContentType = "html"
        };
    }

    private static string StripHtml(string html)
    {
        // Remove script and style blocks
        html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // Convert common elements to markdown
        html = Regex.Replace(html, @"<h1[^>]*>([^<]+)</h1>", "# $1\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<h2[^>]*>([^<]+)</h2>", "## $1\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<h3[^>]*>([^<]+)</h3>", "### $1\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li[^>]*>", "- ", RegexOptions.IgnoreCase);

        // Strip remaining tags
        html = Regex.Replace(html, @"<[^>]+>", "");

        // Decode HTML entities
        html = WebUtility.HtmlDecode(html);

        // Clean up whitespace
        html = Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }
}

/// <summary>
///     Default handler for PDF files using PdfPig.
///     Adds page markers for page-aware chunking.
/// </summary>
public class PdfDocumentHandler : IDocumentHandler
{
    public IReadOnlyList<string> SupportedExtensions => [".pdf"];
    public int Priority => 0;
    public string HandlerName => "PDF";

    public bool CanHandle(string filePath)
    {
        return File.Exists(filePath);
    }

    public Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        var sb = new StringBuilder();
        string? title = null;

        using (var doc = PdfDocument.Open(filePath))
        {
            // Try to get title from metadata
            if (doc.Information?.Title != null)
                title = doc.Information.Title;

            var pageNum = 0;
            foreach (var page in doc.GetPages())
            {
                pageNum++;
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Add page marker for page-aware chunking
                    sb.AppendLine($"<!-- PAGE:{pageNum} -->");

                    // Split text into natural paragraphs
                    // PDFs often have text as continuous lines - try to detect paragraph breaks
                    var normalized = NormalizePdfText(text);
                    sb.AppendLine(normalized);
                    sb.AppendLine();
                }
            }
        }

        return Task.FromResult(new DocumentContent
        {
            Markdown = sb.ToString().Trim(),
            Title = title ?? Path.GetFileNameWithoutExtension(filePath),
            ContentType = "pdf"
        });
    }

    /// <summary>
    ///     Normalize PDF text by detecting and inserting paragraph breaks.
    ///     PDFs often have text without proper paragraph separations.
    /// </summary>
    private static string NormalizePdfText(string text)
    {
        // PDFs extracted with PdfPig often have lines without paragraph breaks
        // Heuristics to detect paragraph breaks:
        // 1. Short lines followed by capital letters (likely new paragraph)
        // 2. Lines ending with sentence-ending punctuation
        // 3. Blank lines

        var lines = text.Split('\n');
        var result = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                // Blank line = paragraph break
                result.AppendLine();
                result.AppendLine();
                continue;
            }

            result.Append(line);

            // Check if this line ends a paragraph
            var endsWithPunctuation = line.EndsWith('.') || line.EndsWith('!') ||
                                      line.EndsWith('?') || line.EndsWith('"') ||
                                      line.EndsWith('\'');
            var isShortLine = line.Length < 60; // Likely a heading or paragraph end
            var nextIsCapital = i + 1 < lines.Length &&
                                lines[i + 1].TrimStart().Length > 0 &&
                                char.IsUpper(lines[i + 1].TrimStart()[0]);

            if (endsWithPunctuation && (isShortLine || nextIsCapital))
            {
                // Likely paragraph break
                result.AppendLine();
                result.AppendLine();
            }
            else if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
            {
                // Continue same paragraph with space
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

/// <summary>
///     Default handler for DOCX files using Open XML SDK
/// </summary>
public class DocxDocumentHandler : IDocumentHandler
{
    public IReadOnlyList<string> SupportedExtensions => [".docx", ".doc"];
    public int Priority => 0;
    public string HandlerName => "DOCX";

    public bool CanHandle(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        // Only handle .docx (Open XML), not .doc (binary)
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".docx";
    }

    public Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        var sb = new StringBuilder();
        string? title = null;

        using (var doc = WordprocessingDocument.Open(filePath, false))
        {
            // Try to get title from core properties
            if (doc.PackageProperties?.Title != null)
                title = doc.PackageProperties.Title;

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body != null)
                foreach (var para in body.Descendants<Paragraph>())
                {
                    var text = para.InnerText;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Check if it's a heading by looking at paragraph properties
                        var style = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                        if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            var level = style.Replace("Heading", "").Trim();
                            if (int.TryParse(level, out var headingLevel) && headingLevel >= 1 && headingLevel <= 6)
                            {
                                sb.Append(new string('#', headingLevel));
                                sb.Append(' ');
                            }
                        }

                        sb.AppendLine(text);
                    }
                }
        }

        return Task.FromResult(new DocumentContent
        {
            Markdown = sb.ToString().Trim(),
            Title = title ?? Path.GetFileNameWithoutExtension(filePath),
            ContentType = "docx"
        });
    }
}

/// <summary>
///     Extension methods to register default document handlers
/// </summary>
public static class DefaultDocumentHandlerExtensions
{
    /// <summary>
    ///     Register all default document handlers with the registry
    /// </summary>
    public static IDocumentHandlerRegistry RegisterDefaultHandlers(this IDocumentHandlerRegistry registry)
    {
        registry.Register(new TextDocumentHandler());
        registry.Register(new HtmlDocumentHandler());
        registry.Register(new PdfDocumentHandler());
        registry.Register(new DocxDocumentHandler());
        registry.Register(new PptxDocumentHandler());
        return registry;
    }
}
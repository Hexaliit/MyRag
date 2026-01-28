using System.Text.RegularExpressions;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Handles writing output to different formats and destinations.
/// Extensible for future formats (PDF, Word, etc.)
/// </summary>
public class OutputWriter
{
    private readonly TemplateService _templateService;
    private readonly Dictionary<string, IOutputFormatter> _formatters = new(StringComparer.OrdinalIgnoreCase);

    public OutputWriter(TemplateService templateService)
    {
        _templateService = templateService;

        // Register built-in formatters
        RegisterFormatter(new MarkdownFormatter());
        RegisterFormatter(new TextFormatter());
        RegisterFormatter(new HtmlFormatter());
        RegisterFormatter(new JsonFormatter());
    }

    public void RegisterFormatter(IOutputFormatter formatter)
    {
        foreach (var ext in formatter.SupportedExtensions)
        {
            _formatters[ext] = formatter;
        }
    }

    /// <summary>
    /// Write output to file, auto-detecting format from extension.
    /// </summary>
    public async Task WriteToFileAsync(DigestData data, string filePath, string? templateName = null)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        // Auto-select template based on extension if not specified
        templateName ??= extension switch
        {
            ".html" or ".htm" => "email",
            ".json" => "json",
            ".md" => "file",
            ".txt" => "compact",
            _ => "file"
        };

        var content = _templateService.Render(data, templateName);

        // Apply formatter if available
        if (_formatters.TryGetValue(extension, out var formatter))
        {
            content = await formatter.FormatAsync(content, data);
        }

        // Ensure directory exists
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, content);
    }

    /// <summary>
    /// Write output to console with optional formatting.
    /// </summary>
    public void WriteToConsole(DigestData data, string templateName = "console", bool usePanel = true)
    {
        var content = _templateService.Render(data, templateName);

        if (usePanel)
        {
            var escapedContent = Markup.Escape(content);
            AnsiConsole.Write(new Panel(escapedContent)
                .Header($"[bold cyan]Doom Scroll Digest ({data.Vibe})[/]")
                .Border(BoxBorder.Rounded)
                .Padding(1, 1));
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    /// <summary>
    /// Get raw rendered content for a template.
    /// </summary>
    public string Render(DigestData data, string templateName = "default")
    {
        return _templateService.Render(data, templateName);
    }

    public IEnumerable<string> GetSupportedExtensions() => _formatters.Keys.Distinct();
}

/// <summary>
/// Interface for output formatters.
/// Implement this to add support for new output formats.
/// </summary>
public interface IOutputFormatter
{
    string[] SupportedExtensions { get; }
    Task<string> FormatAsync(string content, DigestData data);
}

/// <summary>
/// Markdown formatter - passes through as-is.
/// </summary>
public class MarkdownFormatter : IOutputFormatter
{
    public string[] SupportedExtensions => [".md", ".markdown"];

    public Task<string> FormatAsync(string content, DigestData data)
    {
        return Task.FromResult(content);
    }
}

/// <summary>
/// Plain text formatter - strips markdown.
/// </summary>
public partial class TextFormatter : IOutputFormatter
{
    public string[] SupportedExtensions => [".txt"];

    public Task<string> FormatAsync(string content, DigestData data)
    {
        // Strip markdown formatting
        var text = content;

        // Remove links but keep text: [text](url) -> text
        text = MarkdownLinkRegex().Replace(text, "$1");

        // Remove emphasis: **text** or *text* -> text
        text = BoldItalicRegex().Replace(text, "$1");

        // Remove headers: # text -> text
        text = HeadingPrefixRegex().Replace(text, "");

        // Remove horizontal rules
        text = HorizontalRuleRegex().Replace(text, "");

        // Remove blockquotes
        text = BlockquoteRegex().Replace(text, "");

        return Task.FromResult(text.Trim());
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"\*+([^\*]+)\*+")]
    private static partial Regex BoldItalicRegex();

    [GeneratedRegex(@"^#+\s*", RegexOptions.Multiline)]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"^---+$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^\s*>\s*", RegexOptions.Multiline)]
    private static partial Regex BlockquoteRegex();
}

/// <summary>
/// HTML formatter - wraps in basic HTML if not already HTML.
/// </summary>
public partial class HtmlFormatter : IOutputFormatter
{
    public string[] SupportedExtensions => [".html", ".htm"];

    public Task<string> FormatAsync(string content, DigestData data)
    {
        // If content already starts with DOCTYPE or html tag, return as-is
        if (content.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(content);
        }

        // Convert markdown to basic HTML
        var html = ConvertMarkdownToHtml(content);

        var title = $"Doom-Scroll Digest - {data.Date:yyyy-MM-dd}";
        return Task.FromResult($$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{title}}</title>
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 800px; margin: 0 auto; padding: 20px; line-height: 1.6; }
                    h1, h2, h3 { color: #333; }
                    a { color: #007bff; text-decoration: none; }
                    a:hover { text-decoration: underline; }
                    blockquote { border-left: 3px solid #ccc; margin: 0; padding-left: 1em; color: #666; }
                    code { background: #f4f4f4; padding: 2px 6px; border-radius: 3px; }
                    hr { border: none; border-top: 1px solid #eee; margin: 2em 0; }
                </style>
            </head>
            <body>
            {{html}}
            </body>
            </html>
            """);
    }

    private static string ConvertMarkdownToHtml(string markdown)
    {
        var html = markdown;

        // Headers
        html = H3TagRegex().Replace(html, "<h3>$1</h3>");
        html = H2TagRegex().Replace(html, "<h2>$1</h2>");
        html = H1TagRegex().Replace(html, "<h1>$1</h1>");

        // Links: [text](url)
        html = HtmlLinkRegex().Replace(html, "<a href=\"$2\">$1</a>");

        // Bold: **text**
        html = HtmlBoldRegex().Replace(html, "<strong>$1</strong>");

        // Italic: *text*
        html = HtmlItalicRegex().Replace(html, "<em>$1</em>");

        // Blockquotes: > text
        html = HtmlBlockquoteRegex().Replace(html, "<blockquote>$1</blockquote>");

        // List items: - text
        html = HtmlListItemRegex().Replace(html, "<li>$1</li>");

        // Horizontal rules
        html = HtmlHrRegex().Replace(html, "<hr>");

        // Paragraphs (double newlines)
        html = ParagraphBreakRegex().Replace(html, "</p><p>");
        html = $"<p>{html}</p>";

        // Clean up empty paragraphs
        html = EmptyParagraphRegex().Replace(html, "");

        return html;
    }

    [GeneratedRegex(@"^### (.+)$", RegexOptions.Multiline)]
    private static partial Regex H3TagRegex();

    [GeneratedRegex(@"^## (.+)$", RegexOptions.Multiline)]
    private static partial Regex H2TagRegex();

    [GeneratedRegex(@"^# (.+)$", RegexOptions.Multiline)]
    private static partial Regex H1TagRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^\)]+)\)")]
    private static partial Regex HtmlLinkRegex();

    [GeneratedRegex(@"\*\*([^\*]+)\*\*")]
    private static partial Regex HtmlBoldRegex();

    [GeneratedRegex(@"\*([^\*]+)\*")]
    private static partial Regex HtmlItalicRegex();

    [GeneratedRegex(@"^\s*>\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex HtmlBlockquoteRegex();

    [GeneratedRegex(@"^- (.+)$", RegexOptions.Multiline)]
    private static partial Regex HtmlListItemRegex();

    [GeneratedRegex(@"^---+$", RegexOptions.Multiline)]
    private static partial Regex HtmlHrRegex();

    [GeneratedRegex(@"\n\n+")]
    private static partial Regex ParagraphBreakRegex();

    [GeneratedRegex(@"<p>\s*</p>")]
    private static partial Regex EmptyParagraphRegex();
}

/// <summary>
/// JSON formatter - ensures valid JSON output.
/// </summary>
public class JsonFormatter : IOutputFormatter
{
    public string[] SupportedExtensions => [".json"];

    public Task<string> FormatAsync(string content, DigestData data)
    {
        // If template already produced JSON, validate and return
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(content);
            return Task.FromResult(content);
        }
        catch
        {
            // Not valid JSON, serialize the data directly
            var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            return Task.FromResult(json);
        }
    }
}

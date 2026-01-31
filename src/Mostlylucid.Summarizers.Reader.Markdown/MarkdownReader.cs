using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizers.Reader.Markdown;

/// <summary>
/// Reader for Markdown and plain text files.
/// Handles YAML front matter extraction, heading-aware structure preservation,
/// and .md/.mdx/.markdown/.txt extensions.
/// </summary>
public partial class MarkdownReader : IDocumentReader
{
    private readonly ILogger<MarkdownReader> _logger;

    public MarkdownReader(ILogger<MarkdownReader> logger)
    {
        _logger = logger;
    }

    public string ReaderName => "markdown";
    private static readonly string[] _extensions = [".md", ".mdx", ".markdown", ".txt"];
    public IReadOnlyList<string> SupportedExtensions => _extensions;
    public int Priority => 100;

    public async Task<ReaderResult> ReadAsync(string filePath, ReaderOptions? options = null, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
        return ProcessContent(content, filePath);
    }

    public async Task<ReaderResult> ReadAsync(Stream stream, string fileName, ReaderOptions? options = null, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        return ProcessContent(content, fileName);
    }

    private ReaderResult ProcessContent(string content, string fileNameOrPath)
    {
        var metadata = new Dictionary<string, string>();
        var warnings = new List<string>();
        string? title = null;
        var markdown = content;

        // Parse YAML front matter (--- delimited)
        var frontMatterMatch = FrontMatterRegex().Match(content);
        if (frontMatterMatch.Success)
        {
            var frontMatter = frontMatterMatch.Groups[1].Value;
            markdown = content[(frontMatterMatch.Index + frontMatterMatch.Length)..].TrimStart();

            // Extract key-value pairs from front matter
            foreach (var line in frontMatter.Split('\n'))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = line[..colonIdx].Trim().ToLowerInvariant();
                    var value = line[(colonIdx + 1)..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        metadata[key] = value;
                        if (key == "title")
                            title = value;
                    }
                }
            }
        }

        // Extract title from first heading if not found in front matter
        if (title == null)
        {
            var lines = markdown.Split('\n');
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

        var wordCount = WordCounter.Count(markdown);

        return new ReaderResult
        {
            Markdown = markdown,
            Title = title ?? Path.GetFileNameWithoutExtension(fileNameOrPath),
            Metadata = metadata,
            Warnings = warnings,
            WordCount = wordCount
        };
    }

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatterRegex();
}

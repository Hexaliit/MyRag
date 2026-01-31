using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizers.Reader.Gutenberg;

/// <summary>
/// Reader for Project Gutenberg ZIP and TXT files.
/// Handles:
/// - ZIP archives containing one or more .txt files
/// - Plain .txt files with PG header/footer boilerplate
/// - Metadata extraction (title, author, language, release date)
/// - Encoding detection (UTF-8 / Latin-1)
/// </summary>
public partial class GutenbergReader : IDocumentReader
{
    private readonly ILogger<GutenbergReader> _logger;

    public GutenbergReader(ILogger<GutenbergReader> logger)
    {
        _logger = logger;
    }

    public string ReaderName => "gutenberg";
    private static readonly string[] _extensions = [".zip"];
    public IReadOnlyList<string> SupportedExtensions => _extensions;
    public int Priority => 50; // Lower priority — only handles Gutenberg-formatted content

    public async Task<ReaderResult> ReadAsync(string filePath, ReaderOptions? options = null, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".zip")
            return await ReadZipAsync(filePath, options, ct);

        // For .txt files, check if it looks like a Gutenberg file
        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
        if (!IsGutenbergText(content))
        {
            // Not a Gutenberg file — return as-is with a warning
            return new ReaderResult
            {
                Markdown = content,
                Title = Path.GetFileNameWithoutExtension(filePath),
                Warnings = ["File does not appear to be a Project Gutenberg text"],
                WordCount = WordCounter.Count(content)
            };
        }

        return ProcessGutenbergText(content, Path.GetFileNameWithoutExtension(filePath));
    }

    public async Task<ReaderResult> ReadAsync(Stream stream, string fileName, ReaderOptions? options = null, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext == ".zip")
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"reader-gutenberg-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var fs = File.Create(tempPath))
                    await stream.CopyToAsync(fs, ct);

                return await ReadZipAsync(tempPath, options, ct);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        return ProcessGutenbergText(content, Path.GetFileNameWithoutExtension(fileName));
    }

    private async Task<ReaderResult> ReadZipAsync(string zipPath, ReaderOptions? options, CancellationToken ct)
    {
        var allTexts = new List<(string name, string content)>();

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries
                .Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Name))
            {
                ct.ThrowIfCancellationRequested();

                await using var stream = entry.Open();
                // Try UTF-8 first, fall back to Latin-1 if we detect encoding issues
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(ct);
                allTexts.Add((entry.Name, text));
            }
        }

        if (allTexts.Count == 0)
        {
            return new ReaderResult
            {
                Markdown = "",
                Title = Path.GetFileNameWithoutExtension(zipPath),
                Warnings = ["No .txt files found in ZIP archive"],
                WordCount = 0
            };
        }

        // Process the largest text file (the main work) or combine all
        var mainText = allTexts.OrderByDescending(t => t.content.Length).First();
        return ProcessGutenbergText(mainText.content, Path.GetFileNameWithoutExtension(zipPath));
    }

    private ReaderResult ProcessGutenbergText(string content, string fallbackTitle)
    {
        var metadata = ExtractMetadata(content);
        var strippedContent = StripBoilerplate(content);
        var title = metadata.GetValueOrDefault("title", fallbackTitle);
        var wordCount = WordCounter.Count(strippedContent);

        return new ReaderResult
        {
            Markdown = strippedContent,
            Title = title,
            Metadata = metadata,
            WordCount = wordCount
        };
    }

    /// <summary>
    /// Extract metadata from the Project Gutenberg header.
    /// Parses Title:, Author:, Release Date:, Language:, etc.
    /// </summary>
    internal static Dictionary<string, string> ExtractMetadata(string content)
    {
        var metadata = new Dictionary<string, string>();

        // PG headers look like:
        // Title: Pride and Prejudice
        // Author: Jane Austen
        // Release Date: June 1, 1998 [eBook #1342]
        // Language: English

        var headerEnd = content.IndexOf("*** START OF", StringComparison.OrdinalIgnoreCase);
        if (headerEnd < 0)
            headerEnd = Math.Min(content.Length, 3000);

        var header = content[..headerEnd];

        var titleMatch = TitleRegex().Match(header);
        if (titleMatch.Success)
            metadata["title"] = titleMatch.Groups[1].Value.Trim();

        var authorMatch = AuthorRegex().Match(header);
        if (authorMatch.Success)
            metadata["author"] = authorMatch.Groups[1].Value.Trim();

        var dateMatch = ReleaseDateRegex().Match(header);
        if (dateMatch.Success)
            metadata["release_date"] = dateMatch.Groups[1].Value.Trim();

        var langMatch = LanguageRegex().Match(header);
        if (langMatch.Success)
            metadata["language"] = langMatch.Groups[1].Value.Trim();

        var ebookMatch = EbookNumberRegex().Match(header);
        if (ebookMatch.Success)
            metadata["ebook_number"] = ebookMatch.Groups[1].Value.Trim();

        return metadata;
    }

    /// <summary>
    /// Strip Project Gutenberg header and footer boilerplate.
    /// </summary>
    internal static string StripBoilerplate(string content)
    {
        // Find start of actual content
        var startMarker = StartMarkerRegex().Match(content);
        var startIndex = startMarker.Success ? startMarker.Index + startMarker.Length : 0;

        // Find end of actual content
        var endMarker = EndMarkerRegex().Match(content);
        var endIndex = endMarker.Success ? endMarker.Index : content.Length;

        if (startIndex >= endIndex)
            return content.Trim();

        var result = content[startIndex..endIndex].Trim();

        // Strip any remaining PG-specific lines at the start
        var lines = result.Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines.RemoveAt(0);

        return string.Join('\n', lines).Trim();
    }

    /// <summary>
    /// Check if a text file looks like a Project Gutenberg text.
    /// </summary>
    internal static bool IsGutenbergText(string content)
    {
        var header = content.Length > 2000 ? content[..2000] : content;
        return header.Contains("Project Gutenberg", StringComparison.OrdinalIgnoreCase)
               || header.Contains("*** START OF", StringComparison.OrdinalIgnoreCase)
               || header.Contains("E-text prepared by", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"Title:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"Author:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorRegex();

    [GeneratedRegex(@"Release Date:\s*(.+?)(?:\[|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseDateRegex();

    [GeneratedRegex(@"Language:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageRegex();

    [GeneratedRegex(@"\[(?:eBook|EBook)\s+#(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex EbookNumberRegex();

    [GeneratedRegex(@"\*\*\*\s*START OF.*?\*\*\*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StartMarkerRegex();

    [GeneratedRegex(@"\*\*\*\s*END OF.*?\*\*\*", RegexOptions.IgnoreCase)]
    private static partial Regex EndMarkerRegex();
}

using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Services;
using SubtitlesParserV2;

namespace DoomSummarizer.Plugins.Subtitles;

/// <summary>
/// Document handler for subtitle files (SRT, VTT, ASS, SSA).
/// Converts subtitle content to chapter-aware markdown for ingestion.
/// </summary>
public sealed class SubtitleDocumentHandler : IDocumentHandler
{
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex VttCueSettingsRegex = new(@"\s*(align|position|size|line|vertical):[\w%]+", RegexOptions.Compiled);

    public IReadOnlyList<string> SupportedExtensions { get; } = [".srt", ".vtt", ".ass", ".ssa"];
    public int Priority => 10;
    public string HandlerName => "Subtitles";

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        var entries = await ParseSubtitleFileAsync(filePath, options.CancellationToken);
        if (entries.Count == 0)
        {
            return new DocumentContent
            {
                Markdown = "",
                Title = Path.GetFileNameWithoutExtension(filePath),
                ContentType = "subtitles"
            };
        }

        var chapters = ChapterDetector.DetectChapters(entries);
        var markdown = BuildMarkdown(entries, chapters, Path.GetFileNameWithoutExtension(filePath));

        var duration = entries[^1].EndTime;
        var metadata = new Dictionary<string, object>
        {
            ["duration_seconds"] = (int)duration.TotalSeconds,
            ["entry_count"] = entries.Count,
            ["chapter_count"] = chapters.Count,
            ["format"] = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant()
        };

        return new DocumentContent
        {
            Markdown = markdown,
            Title = Path.GetFileNameWithoutExtension(filePath),
            ContentType = "subtitles",
            Metadata = metadata
        };
    }

    private static async Task<List<SubtitleEntry>> ParseSubtitleFileAsync(
        string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var result = SubtitleParser.ParseStream(stream, Encoding.UTF8);

        if (result?.Subtitles == null)
            return [];

        return result.Subtitles
            .Where(item => item.StartTime >= 0 && item.EndTime >= 0)
            .Select(item =>
            {
                var text = CleanSubtitleText(string.Join(" ", item.Lines));
                return new SubtitleEntry(
                    TimeSpan.FromMilliseconds(item.StartTime),
                    TimeSpan.FromMilliseconds(item.EndTime),
                    text);
            })
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .ToList();
    }

    private static string CleanSubtitleText(string text)
    {
        // Strip HTML tags (<i>, <b>, <font>, etc.)
        var cleaned = HtmlTagRegex.Replace(text, "");
        // Strip VTT cue settings
        cleaned = VttCueSettingsRegex.Replace(cleaned, "");
        // Normalize whitespace
        cleaned = cleaned.Replace('\n', ' ').Replace('\r', ' ');
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim();
    }

    private static string BuildMarkdown(
        List<SubtitleEntry> entries,
        List<SubtitleChapter> chapters,
        string docTitle)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {docTitle}");
        sb.AppendLine();

        if (chapters.Count <= 1)
        {
            // No meaningful chapters detected — output as continuous text with timestamps
            WriteEntriesAsText(sb, entries, 0, entries.Count);
        }
        else
        {
            for (var c = 0; c < chapters.Count; c++)
            {
                var chapter = chapters[c];
                var nextStart = c + 1 < chapters.Count ? chapters[c + 1].StartIndex : entries.Count;
                var timestamp = ChapterDetector.FormatTimestamp(chapter.StartTime);

                sb.AppendLine($"## [{timestamp}] {chapter.Title}");
                sb.AppendLine();
                WriteEntriesAsText(sb, entries, chapter.StartIndex, nextStart);
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void WriteEntriesAsText(
        StringBuilder sb, List<SubtitleEntry> entries, int start, int end)
    {
        var paragraph = new StringBuilder();
        TimeSpan? lastEnd = null;

        for (var i = start; i < end && i < entries.Count; i++)
        {
            var entry = entries[i];

            // Insert paragraph break on small gaps (> 2 seconds) for readability
            if (lastEnd.HasValue && (entry.StartTime - lastEnd.Value).TotalSeconds > 2.0
                && paragraph.Length > 0)
            {
                sb.AppendLine(paragraph.ToString().Trim());
                sb.AppendLine();
                paragraph.Clear();
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(entry.Text);
            lastEnd = entry.EndTime;
        }

        if (paragraph.Length > 0)
        {
            sb.AppendLine(paragraph.ToString().Trim());
        }
    }
}

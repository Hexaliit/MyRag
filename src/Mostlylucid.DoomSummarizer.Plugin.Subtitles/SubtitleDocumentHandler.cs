using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Services;
using SubtitlesParserV2;

namespace DoomSummarizer.Plugins.Subtitles;

/// <summary>
/// Document handler for subtitle files (SRT, VTT, ASS, SSA).
/// Converts subtitle content to chapter-aware markdown for ingestion.
/// </summary>
public sealed partial class SubtitleDocumentHandler : IDocumentHandler
{
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s*(align|position|size|line|vertical):[\w%+\-.,]+")]
    private static partial Regex VttCueSettingsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

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

        // Use extension-based format selection to avoid misdetection
        // (e.g., LRC parser incorrectly matching ASS bracket syntax)
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var formatType = SubtitleFormat.GetFormatTypeByFileExtensionName(ext);
        var result = formatType.HasValue
            ? SubtitleParser.ParseStream(stream, Encoding.UTF8, formatType.Value)
            : SubtitleParser.ParseStream(stream, Encoding.UTF8);

        if (result?.Subtitles == null)
            return [];

        var entries = new List<SubtitleEntry>(result.Subtitles.Count);
        var lineJoinBuffer = new StringBuilder(256);

        foreach (var item in result.Subtitles)
        {
            if (item.StartTime < 0 || item.EndTime < 0)
                continue;

            // Join lines into single string using shared buffer
            lineJoinBuffer.Clear();
            for (var j = 0; j < item.Lines.Count; j++)
            {
                if (j > 0) lineJoinBuffer.Append(' ');
                lineJoinBuffer.Append(item.Lines[j]);
            }

            var text = CleanSubtitleText(lineJoinBuffer.ToString());
            if (!string.IsNullOrWhiteSpace(text))
            {
                entries.Add(new SubtitleEntry(
                    TimeSpan.FromMilliseconds(item.StartTime),
                    TimeSpan.FromMilliseconds(item.EndTime),
                    text));
            }
        }

        return entries;
    }

    private static string CleanSubtitleText(string text)
    {
        // Strip HTML tags (<i>, <b>, <font>, etc.)
        var cleaned = HtmlTagRegex().Replace(text, "");
        // Strip VTT cue settings
        cleaned = VttCueSettingsRegex().Replace(cleaned, "");
        // Normalize whitespace (single pass with source-generated regex)
        cleaned = WhitespaceRegex().Replace(cleaned, " ");
        return cleaned.Trim();
    }

    private static string BuildMarkdown(
        List<SubtitleEntry> entries,
        List<SubtitleChapter> chapters,
        string docTitle)
    {
        // Pre-size StringBuilder: ~100 chars per entry as rough estimate
        var sb = new StringBuilder(entries.Count * 100);
        sb.Append("# ").AppendLine(docTitle);
        sb.AppendLine();

        if (chapters.Count <= 1)
        {
            WriteEntriesAsText(sb, entries, 0, entries.Count);
        }
        else
        {
            for (var c = 0; c < chapters.Count; c++)
            {
                var chapter = chapters[c];
                var nextStart = c + 1 < chapters.Count ? chapters[c + 1].StartIndex : entries.Count;
                var timestamp = ChapterDetector.FormatTimestamp(chapter.StartTime);

                sb.Append("## [").Append(timestamp).Append("] ").AppendLine(chapter.Title);
                sb.AppendLine();
                WriteEntriesAsText(sb, entries, chapter.StartIndex, nextStart);
                sb.AppendLine();
            }
        }

        // TrimEnd without allocating a new string when possible
        var result = sb.ToString();
        return result.TrimEnd();
    }

    private static void WriteEntriesAsText(
        StringBuilder sb, List<SubtitleEntry> entries, int start, int end)
    {
        var paragraph = new StringBuilder(512);
        TimeSpan? lastEnd = null;

        for (var i = start; i < end && i < entries.Count; i++)
        {
            var entry = entries[i];

            // Insert paragraph break on small gaps (> 2 seconds) for readability
            if (lastEnd.HasValue && (entry.StartTime - lastEnd.Value).TotalSeconds > 2.0
                && paragraph.Length > 0)
            {
                // Trim trailing space from paragraph before appending
                while (paragraph.Length > 0 && paragraph[^1] == ' ')
                    paragraph.Length--;
                sb.AppendLine(paragraph.ToString());
                sb.AppendLine();
                paragraph.Clear();
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(entry.Text);
            lastEnd = entry.EndTime;
        }

        if (paragraph.Length > 0)
        {
            while (paragraph.Length > 0 && paragraph[^1] == ' ')
                paragraph.Length--;
            sb.AppendLine(paragraph.ToString());
        }
    }
}

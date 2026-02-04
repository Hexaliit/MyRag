using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Pipeline;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services;

/// <summary>
///     Processes subtitle files and integrates them with the RAG pipeline.
///     Converts SRT subtitles to content chunks for indexing.
/// </summary>
public partial class SubtitleProcessingService
{
    private readonly ILogger<SubtitleProcessingService> _logger;

    public SubtitleProcessingService(ILogger<SubtitleProcessingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Parse an SRT file into subtitle entries.
    /// </summary>
    public async Task<List<SrtSubtitleEntry>> ParseSrtAsync(
        string srtPath,
        CancellationToken ct = default)
    {
        var entries = new List<SrtSubtitleEntry>();

        if (!File.Exists(srtPath))
        {
            _logger.LogWarning("SRT file not found: {Path}", srtPath);
            return entries;
        }

        try
        {
            var content = await File.ReadAllTextAsync(srtPath, ct);
            entries = ParseSrtContent(content);

            _logger.LogInformation("Parsed {Count} subtitle entries from {Path}",
                entries.Count, srtPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse SRT file: {Path}", srtPath);
        }

        return entries;
    }

    /// <summary>
    ///     Parse SRT content string into subtitle entries.
    /// </summary>
    public List<SrtSubtitleEntry> ParseSrtContent(string content)
    {
        var entries = new List<SrtSubtitleEntry>();

        // Split by double newline (subtitle blocks)
        var blocks = SrtBlockSplitRegex().Split(content);

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block))
                continue;

            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            if (lines.Count < 3)
                continue;

            // First line: sequence number
            if (!int.TryParse(lines[0], out var sequence))
                continue;

            // Second line: timestamp (00:01:20,000 --> 00:01:25,000)
            var timeMatch = SrtTimestampRegex().Match(lines[1]);
            if (!timeMatch.Success)
                continue;

            var startTime = ParseSrtTimestamp(timeMatch.Groups["start"].Value);
            var endTime = ParseSrtTimestamp(timeMatch.Groups["end"].Value);

            // Remaining lines: text
            var text = string.Join(" ", lines.Skip(2));

            // Remove HTML tags and formatting
            text = StripHtmlTags(text);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            entries.Add(new SrtSubtitleEntry
            {
                Sequence = sequence,
                StartTime = startTime,
                EndTime = endTime,
                Text = text
            });
        }

        return entries;
    }

    /// <summary>
    ///     Convert subtitle entries to content chunks for RAG indexing.
    ///     Groups subtitles into chunks based on time windows.
    /// </summary>
    public List<ContentChunk> ToContentChunks(
        List<SrtSubtitleEntry> entries,
        string sourcePath,
        SubtitleFile subtitleFile,
        double chunkWindowSeconds = 60.0)
    {
        var chunks = new List<ContentChunk>();

        if (entries.Count == 0)
            return chunks;

        // Group by time window
        var groups = entries
            .GroupBy(e => (int)(e.StartTime / chunkWindowSeconds))
            .OrderBy(g => g.Key);

        var chunkIndex = 0;
        foreach (var group in groups)
        {
            var groupEntries = group.OrderBy(e => e.StartTime).ToList();
            var text = string.Join(" ", groupEntries.Select(e => e.Text));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var startTime = groupEntries.Min(e => e.StartTime);
            var endTime = groupEntries.Max(e => e.EndTime);

            chunks.Add(new ContentChunk
            {
                Id = $"{sourcePath}:subtitle:{subtitleFile.LanguageCode}:{chunkIndex}",
                Text = text,
                ContentType = ContentType.Transcript,
                SourcePath = sourcePath,
                Index = chunkIndex,
                ContentHash = ComputeHash(text),
                Confidence = 0.9, // SRT subtitles are generally accurate
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "subtitle_srt",
                    ["language"] = subtitleFile.Language,
                    ["language_code"] = subtitleFile.LanguageCode,
                    ["subtitle_source"] = subtitleFile.Source,
                    ["start_time"] = startTime,
                    ["end_time"] = endTime,
                    ["entry_count"] = groupEntries.Count,
                    ["hearing_impaired"] = subtitleFile.HearingImpaired
                }
            });

            chunkIndex++;
        }

        _logger.LogInformation("Created {Count} content chunks from {Entries} subtitle entries",
            chunks.Count, entries.Count);

        return chunks;
    }

    /// <summary>
    ///     Create utterances from subtitle entries.
    ///     These can be used alongside or instead of audio transcription.
    /// </summary>
    public List<Utterance> ToUtterances(
        List<SrtSubtitleEntry> entries,
        Guid videoId,
        string language)
    {
        return entries.Select(e => new Utterance
        {
            Id = Guid.NewGuid(),
            VideoId = videoId,
            Text = e.Text,
            StartTime = e.StartTime,
            EndTime = e.EndTime,
            Confidence = 0.95, // Subtitles are usually professional translations
            Language = language
        }).ToList();
    }

    /// <summary>
    ///     Merge subtitle-derived utterances with transcription utterances.
    ///     Prefers subtitle text when timestamps overlap (usually more accurate).
    /// </summary>
    public List<Utterance> MergeWithTranscription(
        List<Utterance> subtitleUtterances,
        List<Utterance> transcriptionUtterances,
        double overlapThreshold = 0.5)
    {
        if (subtitleUtterances.Count == 0)
            return transcriptionUtterances;

        if (transcriptionUtterances.Count == 0)
            return subtitleUtterances;

        var merged = new List<Utterance>();
        var usedSubtitleIndices = new HashSet<int>();

        foreach (var trans in transcriptionUtterances)
        {
            // Find overlapping subtitle
            var bestMatch = -1;
            var bestOverlap = 0.0;

            for (var i = 0; i < subtitleUtterances.Count; i++)
            {
                if (usedSubtitleIndices.Contains(i))
                    continue;

                var sub = subtitleUtterances[i];
                var overlap = ComputeTimeOverlap(trans, sub);

                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestMatch = i;
                }
            }

            if (bestMatch >= 0 && bestOverlap >= overlapThreshold)
            {
                // Use subtitle text (usually more accurate)
                var sub = subtitleUtterances[bestMatch];
                merged.Add(sub with
                {
                    // Keep transcription speaker info if available
                    SpeakerId = trans.SpeakerId ?? sub.SpeakerId
                });
                usedSubtitleIndices.Add(bestMatch);
            }
            else
            {
                // No matching subtitle, keep transcription
                merged.Add(trans);
            }
        }

        // Add remaining subtitles that didn't match any transcription
        for (var i = 0; i < subtitleUtterances.Count; i++)
            if (!usedSubtitleIndices.Contains(i))
                merged.Add(subtitleUtterances[i]);

        return merged.OrderBy(u => u.StartTime).ToList();
    }

    private static double ComputeTimeOverlap(Utterance a, Utterance b)
    {
        var overlapStart = Math.Max(a.StartTime, b.StartTime);
        var overlapEnd = Math.Min(a.EndTime, b.EndTime);

        if (overlapEnd <= overlapStart)
            return 0;

        var overlapDuration = overlapEnd - overlapStart;
        var minDuration = Math.Min(a.EndTime - a.StartTime, b.EndTime - b.StartTime);

        if (minDuration <= 0)
            return 0;

        return overlapDuration / minDuration;
    }

    private static double ParseSrtTimestamp(string timestamp)
    {
        // Format: 00:01:20,000 or 00:01:20.000
        var parts = timestamp.Replace(',', '.').Split(':');
        if (parts.Length != 3)
            return 0;

        if (!int.TryParse(parts[0], out var hours))
            return 0;
        if (!int.TryParse(parts[1], out var minutes))
            return 0;
        if (!double.TryParse(parts[2], out var seconds))
            return 0;

        return hours * 3600 + minutes * 60 + seconds;
    }

    private static string StripHtmlTags(string text)
    {
        // Remove HTML-style tags like <i>, <b>, <font>
        text = HtmlTagRegex().Replace(text, "");

        // Remove ASS/SSA style tags like {\an8}
        text = AssTagRegex().Replace(text, "");

        // Decode HTML entities
        text = text.Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"");

        return text.Trim();
    }

    private static string ComputeHash(string text)
    {
        var hash = XxHash64.Hash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }

    [GeneratedRegex(@"\r?\n\r?\n", RegexOptions.Multiline)]
    private static partial Regex SrtBlockSplitRegex();

    [GeneratedRegex(@"(?<start>\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2}[,\.]\d{3})")]
    private static partial Regex SrtTimestampRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex AssTagRegex();
}

/// <summary>
///     Single subtitle entry from an SRT file.
/// </summary>
public record SrtSubtitleEntry
{
    public int Sequence { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public string Text { get; init; } = string.Empty;

    public double Duration => EndTime - StartTime;
}
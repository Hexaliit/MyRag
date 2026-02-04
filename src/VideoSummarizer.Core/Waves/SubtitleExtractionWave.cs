using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Stage 2.5: Extract subtitles from video (embedded or external SRT).
///     Creates Utterances for speech text and TextTracks for on-screen text.
///     Subtitle text feeds into DocSummarizer for entity extraction and evidence.
/// </summary>
public class SubtitleExtractionWave : IVideoWave
{
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly ILogger<SubtitleExtractionWave> _logger;

    public SubtitleExtractionWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<SubtitleExtractionWave> logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    public string Name => "subtitle_extraction";
    public int Priority => 750; // After keyframe extraction
    public IReadOnlyList<string> Tags => [VideoSignalTags.Speech, VideoSignalTags.Ocr];

    public bool ShouldRun(VideoContext context)
    {
        return context.Metadata != null;
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Extracting subtitles", 0);

        var videoPath = context.VideoPath;
        var videoDir = Path.GetDirectoryName(videoPath)!;
        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        var subtitleDir = Path.Combine(context.WorkingDirectory, "subtitles");
        Directory.CreateDirectory(subtitleDir);

        var allEntries = new List<SubtitleEntry>();

        // 1. Check for external SRT files (same directory as video)
        context.ReportProgress("Checking for external SRT files", 10);
        var externalSrts = FindExternalSrtFiles(videoDir, videoName);

        foreach (var srtPath in externalSrts)
        {
            _logger.LogInformation("Found external SRT: {Path}", srtPath);
            var entries = _ffmpegService.ParseSrtFile(srtPath);
            allEntries.AddRange(entries);

            context.AddSignal(new VideoSignal
            {
                Key = "subtitle.external_srt",
                Value = Path.GetFileName(srtPath),
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            });
        }

        // 2. Check for embedded subtitles in video
        context.ReportProgress("Checking for embedded subtitles", 30);
        var subtitleStreams = await _ffmpegService.GetSubtitleStreamsAsync(videoPath, ct);

        _logger.LogInformation("Found {Count} embedded subtitle streams", subtitleStreams.Count);

        foreach (var stream in subtitleStreams)
        {
            context.ReportProgress($"Extracting subtitle stream {stream.StreamIndex}", 40);

            var extractedPath = await _ffmpegService.ExtractSubtitlesToSrtAsync(
                videoPath,
                stream.StreamIndex,
                subtitleDir,
                ct);

            if (extractedPath != null)
            {
                var entries = _ffmpegService.ParseSrtFile(extractedPath);
                allEntries.AddRange(entries);

                context.AddSignal(new VideoSignal
                {
                    Key = $"subtitle.embedded.{stream.StreamIndex}",
                    Value = stream.Language ?? "unknown",
                    Source = Name,
                    Metadata = new Dictionary<string, object>
                    {
                        ["codec"] = stream.Codec ?? "unknown",
                        ["title"] = stream.Title ?? "",
                        ["entry_count"] = entries.Count
                    },
                    Tags = [VideoSignalTags.Speech]
                });
            }
        }

        // 3. Deduplicate and merge entries
        context.ReportProgress("Processing subtitle entries", 60);
        var mergedEntries = MergeAndDeduplicateEntries(allEntries);

        _logger.LogInformation("Processed {Raw} raw entries into {Merged} merged entries",
            allEntries.Count, mergedEntries.Count);

        // 4. Create Utterances from subtitle entries
        context.ReportProgress("Creating utterances", 70);
        foreach (var entry in mergedEntries)
        {
            var utterance = new Utterance
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata!.Id,
                Text = entry.Text,
                StartTime = entry.StartTime,
                EndTime = entry.EndTime,
                Confidence = 0.95, // Subtitles are generally accurate
                Language = DetectLanguage(entry.Text)
            };

            // Find overlapping shots
            utterance.ShotIds.AddRange(
                context.Shots
                    .Where(s => s.StartTime < entry.EndTime && s.EndTime > entry.StartTime)
                    .Select(s => s.Id));

            context.Utterances.Add(utterance);
        }

        // 5. Create TextTracks for persistent on-screen text
        context.ReportProgress("Identifying text tracks", 85);
        await CreateTextTracksFromSubtitles(context, mergedEntries, ct);

        // 6. Store full transcript for DocSummarizer
        var fullTranscript = string.Join("\n", mergedEntries.Select(e => e.Text));
        if (!string.IsNullOrEmpty(fullTranscript))
        {
            context.SetCached("subtitle_transcript", fullTranscript);

            // Store as signal for downstream processing
            context.AddSignal(new VideoSignal
            {
                Key = "subtitle.transcript",
                Value = fullTranscript,
                Source = Name,
                Confidence = 0.95,
                Tags = [VideoSignalTags.Speech]
            });
        }

        // Add summary signals
        context.AddSignals([
            new VideoSignal
            {
                Key = "subtitle.utterance_count",
                Value = context.Utterances.Count,
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            },
            new VideoSignal
            {
                Key = "subtitle.total_words",
                Value = mergedEntries.Sum(e => e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            },
            new VideoSignal
            {
                Key = "subtitle.duration_coverage",
                Value = CalculateCoverage(mergedEntries, context.Metadata!.Duration),
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            }
        ]);

        context.ReportProgress("Subtitle extraction complete", 100);
    }

    /// <summary>
    ///     Find external SRT files in the same directory as the video.
    ///     Looks for: video.srt, video.en.srt, video.eng.srt, etc.
    /// </summary>
    private List<string> FindExternalSrtFiles(string directory, string videoName)
    {
        var srtFiles = new List<string>();

        // Patterns to look for
        var patterns = new[]
        {
            $"{videoName}.srt",
            $"{videoName}.en.srt",
            $"{videoName}.eng.srt",
            $"{videoName}.english.srt",
            $"{videoName}.*srt"
        };

        foreach (var pattern in patterns)
            try
            {
                var files = Directory.GetFiles(directory, pattern);
                srtFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error searching for SRT pattern: {Pattern}", pattern);
            }

        return srtFiles.Distinct().ToList();
    }

    /// <summary>
    ///     Merge overlapping entries and deduplicate identical text.
    /// </summary>
    private List<SubtitleEntry> MergeAndDeduplicateEntries(List<SubtitleEntry> entries)
    {
        if (entries.Count == 0) return entries;

        // Sort by start time
        var sorted = entries.OrderBy(e => e.StartTime).ToList();

        // Remove exact duplicates
        var seen = new HashSet<string>();
        var unique = new List<SubtitleEntry>();

        foreach (var entry in sorted)
        {
            var key = $"{entry.StartTime:F2}:{entry.Text.Trim().ToLowerInvariant()}";
            if (seen.Add(key)) unique.Add(entry);
        }

        // Merge adjacent entries with same text
        var merged = new List<SubtitleEntry>();
        SubtitleEntry? current = null;

        foreach (var entry in unique)
            if (current == null)
            {
                current = entry;
            }
            else if (entry.Text.Equals(current.Text, StringComparison.OrdinalIgnoreCase) &&
                     entry.StartTime - current.EndTime < 0.5)
            {
                // Extend current entry
                current = current with { EndTime = entry.EndTime };
            }
            else
            {
                merged.Add(current);
                current = entry;
            }

        if (current != null) merged.Add(current);

        return merged;
    }

    /// <summary>
    ///     Create TextTracks from persistent subtitle text.
    ///     Identifies text that appears multiple times (titles, lower thirds, etc.).
    /// </summary>
    private Task CreateTextTracksFromSubtitles(
        VideoContext context,
        List<SubtitleEntry> entries,
        CancellationToken ct)
    {
        // Group entries by normalized text to find repeated content
        var textGroups = entries
            .GroupBy(e => NormalizeText(e.Text))
            .Where(g => g.Key.Length >= 3); // Ignore very short text

        foreach (var group in textGroups)
        {
            var items = group.ToList();
            if (items.Count == 1 && items[0].Duration < 5.0)
                // Single occurrence, short duration - skip (probably speech)
                continue;

            // This text appears multiple times or has long duration - might be a title/caption
            var textTrack = new TextTrack
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata!.Id,
                Text = items[0].Text,
                CanonicalText = group.Key,
                StartTime = items.Min(i => i.StartTime),
                EndTime = items.Max(i => i.EndTime),
                Confidence = 0.9,
                TextType = ClassifyTextType(items[0].Text, items)
            };

            // Add shot associations
            textTrack.ShotIds.AddRange(
                context.Shots
                    .Where(s => s.StartTime < textTrack.EndTime && s.EndTime > textTrack.StartTime)
                    .Select(s => s.Id));

            context.TextTracks.Add(textTrack);
        }

        _logger.LogInformation("Created {Count} text tracks from subtitles", context.TextTracks.Count);
        return Task.CompletedTask;
    }

    private static string NormalizeText(string text)
    {
        return text.Trim().ToLowerInvariant()
            .Replace("\n", " ")
            .Replace("  ", " ");
    }

    private static TextTrackType ClassifyTextType(string text, List<SubtitleEntry> occurrences)
    {
        // Heuristics for text type classification
        var totalDuration = occurrences.Sum(o => o.Duration);
        var occurrenceCount = occurrences.Count;

        // Long duration, appears once - probably a title slide
        if (occurrenceCount == 1 && totalDuration > 3.0) return TextTrackType.SlideTitle;

        // Short text at start/end - might be intro/outro
        var avgTime = occurrences.Average(o => o.StartTime);
        if (text.Length < 50 && (avgTime < 30 || avgTime > totalDuration * 0.9)) return TextTrackType.LowerThird;

        // Multiple occurrences - might be chapter heading
        if (occurrenceCount > 1) return TextTrackType.SlideContent;

        return TextTrackType.Unknown;
    }

    private static string? DetectLanguage(string text)
    {
        // Very basic language detection based on common words
        // In production, use a proper language detection library
        var lowerText = text.ToLowerInvariant();

        if (lowerText.Contains(" the ") || lowerText.Contains(" is ") || lowerText.Contains(" and "))
            return "en";
        if (lowerText.Contains(" le ") || lowerText.Contains(" la ") || lowerText.Contains(" et "))
            return "fr";
        if (lowerText.Contains(" der ") || lowerText.Contains(" die ") || lowerText.Contains(" und "))
            return "de";
        if (lowerText.Contains(" el ") || lowerText.Contains(" la ") || lowerText.Contains(" y "))
            return "es";

        return null;
    }

    private static double CalculateCoverage(List<SubtitleEntry> entries, double videoDuration)
    {
        if (videoDuration <= 0 || entries.Count == 0) return 0;

        var totalSubtitleTime = entries.Sum(e => e.Duration);
        return Math.Min(1.0, totalSubtitleTime / videoDuration);
    }
}
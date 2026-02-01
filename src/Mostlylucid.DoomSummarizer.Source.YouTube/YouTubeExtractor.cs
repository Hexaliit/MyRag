using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.ClosedCaptions;
using YoutubeExplode.Videos.Streams;

namespace DoomSummarizer.Sources.YouTube;

/// <summary>
/// Delegate for audio transcription. Accepts an audio file path and returns
/// timestamped transcript segments. Allows plugging in Whisper or any other
/// transcription backend without coupling to AudioSummarizer.Core.
/// </summary>
public delegate Task<List<TranscriptSegmentInfo>> AudioTranscriberDelegate(
    string audioFilePath, CancellationToken ct);

/// <summary>
/// A timestamped segment from audio transcription.
/// Deliberately simple to avoid coupling to AudioSummarizer.Core models.
/// </summary>
public record TranscriptSegmentInfo(
    double StartSeconds,
    double EndSeconds,
    string Text,
    double Confidence = 0.0);

/// <summary>
/// Result of YouTube video extraction including metadata and chapter-aware content items.
/// </summary>
public record YouTubeExtractionResult(
    string VideoId,
    string Title,
    string Author,
    string Channel,
    TimeSpan Duration,
    string? Description,
    string? ThumbnailUrl,
    List<ContentItem> Items);

/// <summary>
/// Extracts metadata and subtitles from YouTube videos using YoutubeExplode.
/// No API key required. Supports chapter detection from description timestamps
/// and subtitle gap analysis.
/// </summary>
public sealed class YouTubeExtractor
{
    private static readonly Regex YouTubeUrlRegex = new(
        @"(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/embed/|youtube\.com/v/|youtube\.com/shorts/)([a-zA-Z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly YoutubeClient _youtube = new();

    /// <summary>
    /// Check if a URL is a YouTube video URL.
    /// </summary>
    public static bool IsYouTubeUrl(string url)
        => VideoId.TryParse(url) != null;

    /// <summary>
    /// Extract the video ID from a YouTube URL.
    /// </summary>
    public static string? ExtractVideoId(string url)
        => VideoId.TryParse(url)?.Value;

    /// <summary>
    /// Extract metadata and subtitles from a YouTube video.
    /// When no captions are available and an <paramref name="audioTranscriber"/> is provided,
    /// downloads the audio stream and transcribes it using the supplied backend (e.g. Whisper).
    /// </summary>
    /// <param name="url">YouTube video URL.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="audioTranscriber">
    /// Optional audio transcription delegate. When provided and no captions exist,
    /// the audio track is downloaded to a temp file and passed to this delegate.
    /// </param>
    public async Task<YouTubeExtractionResult> ExtractAsync(
        string url,
        Action<string>? progress = null,
        CancellationToken ct = default,
        AudioTranscriberDelegate? audioTranscriber = null)
    {
        var videoId = VideoId.TryParse(url)
            ?? throw new ArgumentException($"Not a valid YouTube URL: {url}");

        // 1. Get video metadata
        progress?.Invoke("Fetching video metadata...");
        var video = await _youtube.Videos.GetAsync(videoId, ct);

        // 2. Get caption tracks
        progress?.Invoke("Fetching caption tracks...");
        ClosedCaptionManifest? manifest = null;
        try
        {
            manifest = await _youtube.Videos.ClosedCaptions.GetManifestAsync(videoId, ct);
        }
        catch
        {
            // Some videos have no captions at all
        }

        // 3. Pick best caption track
        var track = PickBestTrack(manifest);
        List<ClosedCaption>? captions = null;

        if (track != null)
        {
            progress?.Invoke($"Downloading captions ({track.Language.Name})...");
            var captionTrack = await _youtube.Videos.ClosedCaptions.GetAsync(track, ct);
            captions = captionTrack.Captions.ToList();
        }

        // 3b. Fallback: if no captions and audio transcriber available, download + transcribe
        List<TranscriptSegmentInfo>? transcriptSegments = null;
        if ((captions == null || captions.Count == 0) && audioTranscriber != null)
        {
            transcriptSegments = await TranscribeAudioFallbackAsync(
                videoId, audioTranscriber, progress, ct);
        }

        // 4. Parse chapters from description
        var descriptionChapters = DescriptionChapterParser.Parse(video.Description);
        progress?.Invoke(descriptionChapters.Count > 0
            ? $"Found {descriptionChapters.Count} chapters in description"
            : "No description chapters found");

        // 5. Build content items
        var sourceTag = $"youtube:{SanitizeSourceTag(video.Author.ChannelTitle)}";
        List<ContentItem> items;

        if (captions is { Count: > 0 })
        {
            items = BuildContentItems(video, captions, descriptionChapters, sourceTag, progress);
        }
        else if (transcriptSegments is { Count: > 0 })
        {
            items = BuildContentItemsFromTranscript(
                video, transcriptSegments, descriptionChapters, sourceTag, progress);
        }
        else
        {
            items = BuildContentItems(video, null, descriptionChapters, sourceTag, progress);
        }

        return new YouTubeExtractionResult(
            videoId.Value,
            video.Title,
            video.Author.ChannelTitle,
            video.Author.ChannelTitle,
            video.Duration ?? TimeSpan.Zero,
            video.Description,
            video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url,
            items);
    }

    /// <summary>
    /// Download the audio stream to a temp file and transcribe it.
    /// </summary>
    private async Task<List<TranscriptSegmentInfo>?> TranscribeAudioFallbackAsync(
        VideoId videoId,
        AudioTranscriberDelegate transcriber,
        Action<string>? progress,
        CancellationToken ct)
    {
        string? tempPath = null;
        try
        {
            // Get the best audio-only stream
            progress?.Invoke("No captions found — downloading audio for transcription...");
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
            var audioStream = streamManifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .FirstOrDefault();

            if (audioStream == null)
            {
                progress?.Invoke("No audio stream available");
                return null;
            }

            // Download to temp file
            var ext = audioStream.Container.Name; // e.g. "mp4", "webm"
            tempPath = Path.Combine(Path.GetTempPath(), $"yt-{videoId.Value}.{ext}");
            progress?.Invoke($"Downloading audio ({audioStream.Bitrate.MegaBitsPerSecond:F1} Mbps, {audioStream.Container.Name})...");
            await _youtube.Videos.Streams.DownloadAsync(audioStream, tempPath, cancellationToken: ct);

            // Transcribe
            progress?.Invoke("Transcribing audio with speech-to-text...");
            var segments = await transcriber(tempPath, ct);
            progress?.Invoke($"Transcription complete: {segments.Count} segments");

            return segments.Count > 0 ? segments : null;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"Audio transcription failed: {ex.Message}");
            return null;
        }
        finally
        {
            // Clean up temp file
            if (tempPath != null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort */ }
            }
        }
    }

    private static ClosedCaptionTrackInfo? PickBestTrack(ClosedCaptionManifest? manifest)
    {
        if (manifest == null || manifest.Tracks.Count == 0)
            return null;

        var tracks = manifest.Tracks.ToList();

        // Preference order: manual English > auto English > manual any > auto any
        var manualEnglish = tracks.FirstOrDefault(t =>
            t.Language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
            !t.IsAutoGenerated);

        if (manualEnglish != null) return manualEnglish;

        var autoEnglish = tracks.FirstOrDefault(t =>
            t.Language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
            t.IsAutoGenerated);

        if (autoEnglish != null) return autoEnglish;

        var manualAny = tracks.FirstOrDefault(t => !t.IsAutoGenerated);
        return manualAny ?? tracks.FirstOrDefault();
    }

    private static List<ContentItem> BuildContentItems(
        Video video,
        List<ClosedCaption>? captions,
        List<DescriptionChapter> descriptionChapters,
        string sourceTag,
        Action<string>? progress)
    {
        if (captions == null || captions.Count == 0)
        {
            // No captions — create a single item from metadata only
            progress?.Invoke("No captions available, using metadata only");
            return
            [
                new ContentItem
                {
                    Id = $"yt:{video.Id}:meta",
                    Source = sourceTag,
                    Title = video.Title,
                    Url = video.Url,
                    Content = BuildMetadataContent(video),
                    Summary = video.Description?.Length > 300
                        ? video.Description[..300] + "..."
                        : video.Description ?? video.Title,
                    Author = video.Author.ChannelTitle,
                    IsEnriched = true,
                    CreatedAt = video.UploadDate,
                    FetchedAt = DateTimeOffset.UtcNow,
                    ImageUrl = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url,
                    Metadata = new Dictionary<string, string>
                    {
                        ["video_id"] = video.Id.Value,
                        ["channel"] = video.Author.ChannelTitle,
                        ["duration"] = (video.Duration ?? TimeSpan.Zero).ToString()
                    }
                }
            ];
        }

        // Try chapter-based chunking first (from description), then fall back to gap detection
        if (descriptionChapters.Count >= 2)
        {
            progress?.Invoke($"Chunking by {descriptionChapters.Count} description chapters");
            return ChunkByDescriptionChapters(video, captions, descriptionChapters, sourceTag);
        }

        // Fall back to gap-based chunking
        progress?.Invoke("Chunking by subtitle gaps");
        return ChunkByGaps(video, captions, sourceTag);
    }

    private static List<ContentItem> ChunkByDescriptionChapters(
        Video video,
        List<ClosedCaption> captions,
        List<DescriptionChapter> chapters,
        string sourceTag)
    {
        var items = new List<ContentItem>();

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var nextStart = i + 1 < chapters.Count
                ? chapters[i + 1].Timestamp
                : video.Duration ?? TimeSpan.MaxValue;

            // Collect captions within this chapter's time range
            var chapterCaptions = captions
                .Where(c => c.Offset >= chapter.Timestamp && c.Offset < nextStart)
                .ToList();

            if (chapterCaptions.Count == 0) continue;

            var text = string.Join(" ", chapterCaptions.Select(c => c.Text.Trim()));
            var timestamp = FormatTimestamp(chapter.Timestamp);

            items.Add(new ContentItem
            {
                Id = $"yt:{video.Id}:ch{i}",
                Source = sourceTag,
                Title = $"{video.Title} - [{timestamp}] {chapter.Title}",
                Url = $"{video.Url}&t={(int)chapter.Timestamp.TotalSeconds}",
                Content = text,
                Summary = text.Length > 300 ? text[..300] + "..." : text,
                Author = video.Author.ChannelTitle,
                IsEnriched = true,
                CreatedAt = video.UploadDate,
                FetchedAt = DateTimeOffset.UtcNow,
                ChunkSequence = i,
                ParentDocumentId = $"yt:{video.Id}",
                UnitLevel = "chapter",
                Metadata = new Dictionary<string, string>
                {
                    ["video_id"] = video.Id.Value,
                    ["chapter_index"] = i.ToString(),
                    ["chapter_title"] = chapter.Title,
                    ["timestamp"] = timestamp
                }
            });
        }

        return items;
    }

    private static List<ContentItem> ChunkByGaps(
        Video video,
        List<ClosedCaption> captions,
        string sourceTag,
        double gapThresholdSeconds = 5.0)
    {
        var items = new List<ContentItem>();
        var gapThreshold = TimeSpan.FromSeconds(gapThresholdSeconds);
        var currentChunk = new StringBuilder();
        var chunkStart = captions[0].Offset;
        var chunkIndex = 0;

        for (var i = 0; i < captions.Count; i++)
        {
            var caption = captions[i];
            currentChunk.Append(caption.Text.Trim());
            currentChunk.Append(' ');

            // Check for gap to next caption
            var isLast = i == captions.Count - 1;
            var hasGap = !isLast &&
                (captions[i + 1].Offset - (caption.Offset + caption.Duration)) >= gapThreshold;

            if ((hasGap && currentChunk.Length > 100) || isLast)
            {
                var text = currentChunk.ToString().Trim();
                if (text.Length > 0)
                {
                    var timestamp = FormatTimestamp(chunkStart);
                    var title = text.Length > 60 ? text[..57] + "..." : text;

                    items.Add(new ContentItem
                    {
                        Id = $"yt:{video.Id}:s{chunkIndex}",
                        Source = sourceTag,
                        Title = $"{video.Title} - [{timestamp}] {title}",
                        Url = $"{video.Url}&t={(int)chunkStart.TotalSeconds}",
                        Content = text,
                        Summary = text.Length > 300 ? text[..300] + "..." : text,
                        Author = video.Author.ChannelTitle,
                        IsEnriched = true,
                        CreatedAt = video.UploadDate,
                        FetchedAt = DateTimeOffset.UtcNow,
                        ChunkSequence = chunkIndex,
                        ParentDocumentId = $"yt:{video.Id}",
                        UnitLevel = "section",
                        Metadata = new Dictionary<string, string>
                        {
                            ["video_id"] = video.Id.Value,
                            ["chunk_index"] = chunkIndex.ToString(),
                            ["timestamp"] = timestamp
                        }
                    });

                    chunkIndex++;
                }

                currentChunk.Clear();
                if (!isLast)
                    chunkStart = captions[i + 1].Offset;
            }
        }

        return items;
    }

    /// <summary>
    /// Build content items from audio transcription segments (Whisper fallback path).
    /// Uses description chapters when available, otherwise gaps in transcript timing.
    /// </summary>
    private static List<ContentItem> BuildContentItemsFromTranscript(
        Video video,
        List<TranscriptSegmentInfo> segments,
        List<DescriptionChapter> descriptionChapters,
        string sourceTag,
        Action<string>? progress)
    {
        // Convert transcript segments to the same caption-like format for chapter chunking
        if (descriptionChapters.Count >= 2)
        {
            progress?.Invoke($"Chunking transcription by {descriptionChapters.Count} description chapters");
            return ChunkTranscriptByChapters(video, segments, descriptionChapters, sourceTag);
        }

        progress?.Invoke("Chunking transcription by timing gaps");
        return ChunkTranscriptByGaps(video, segments, sourceTag);
    }

    private static List<ContentItem> ChunkTranscriptByChapters(
        Video video,
        List<TranscriptSegmentInfo> segments,
        List<DescriptionChapter> chapters,
        string sourceTag)
    {
        var items = new List<ContentItem>();

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var nextStartSec = i + 1 < chapters.Count
                ? chapters[i + 1].Timestamp.TotalSeconds
                : (video.Duration ?? TimeSpan.MaxValue).TotalSeconds;

            var chapterSegments = segments
                .Where(s => s.StartSeconds >= chapter.Timestamp.TotalSeconds && s.StartSeconds < nextStartSec)
                .ToList();

            if (chapterSegments.Count == 0) continue;

            var text = string.Join(" ", chapterSegments.Select(s => s.Text.Trim()));
            var timestamp = FormatTimestamp(chapter.Timestamp);
            var avgConfidence = chapterSegments.Average(s => s.Confidence);

            items.Add(new ContentItem
            {
                Id = $"yt:{video.Id}:tch{i}",
                Source = sourceTag,
                Title = $"{video.Title} - [{timestamp}] {chapter.Title}",
                Url = $"{video.Url}&t={(int)chapter.Timestamp.TotalSeconds}",
                Content = text,
                Summary = text.Length > 300 ? text[..300] + "..." : text,
                Author = video.Author.ChannelTitle,
                IsEnriched = true,
                CreatedAt = video.UploadDate,
                FetchedAt = DateTimeOffset.UtcNow,
                ChunkSequence = i,
                ParentDocumentId = $"yt:{video.Id}",
                UnitLevel = "chapter",
                Metadata = new Dictionary<string, string>
                {
                    ["video_id"] = video.Id.Value,
                    ["chapter_index"] = i.ToString(),
                    ["chapter_title"] = chapter.Title,
                    ["timestamp"] = timestamp,
                    ["source_method"] = "audio_transcription",
                    ["confidence"] = avgConfidence.ToString("F3")
                }
            });
        }

        return items;
    }

    private static List<ContentItem> ChunkTranscriptByGaps(
        Video video,
        List<TranscriptSegmentInfo> segments,
        string sourceTag,
        double gapThresholdSeconds = 5.0)
    {
        var items = new List<ContentItem>();
        var currentChunk = new StringBuilder();
        var chunkStartSec = segments[0].StartSeconds;
        var chunkIndex = 0;
        var chunkConfidences = new List<double>();

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            currentChunk.Append(seg.Text.Trim());
            currentChunk.Append(' ');
            chunkConfidences.Add(seg.Confidence);

            var isLast = i == segments.Count - 1;
            var hasGap = !isLast &&
                (segments[i + 1].StartSeconds - seg.EndSeconds) >= gapThresholdSeconds;

            if ((hasGap && currentChunk.Length > 100) || isLast)
            {
                var text = currentChunk.ToString().Trim();
                if (text.Length > 0)
                {
                    var chunkStart = TimeSpan.FromSeconds(chunkStartSec);
                    var timestamp = FormatTimestamp(chunkStart);
                    var title = text.Length > 60 ? text[..57] + "..." : text;
                    var avgConfidence = chunkConfidences.Count > 0
                        ? chunkConfidences.Average() : 0.0;

                    items.Add(new ContentItem
                    {
                        Id = $"yt:{video.Id}:ts{chunkIndex}",
                        Source = sourceTag,
                        Title = $"{video.Title} - [{timestamp}] {title}",
                        Url = $"{video.Url}&t={(int)chunkStartSec}",
                        Content = text,
                        Summary = text.Length > 300 ? text[..300] + "..." : text,
                        Author = video.Author.ChannelTitle,
                        IsEnriched = true,
                        CreatedAt = video.UploadDate,
                        FetchedAt = DateTimeOffset.UtcNow,
                        ChunkSequence = chunkIndex,
                        ParentDocumentId = $"yt:{video.Id}",
                        UnitLevel = "section",
                        Metadata = new Dictionary<string, string>
                        {
                            ["video_id"] = video.Id.Value,
                            ["chunk_index"] = chunkIndex.ToString(),
                            ["timestamp"] = timestamp,
                            ["source_method"] = "audio_transcription",
                            ["confidence"] = avgConfidence.ToString("F3")
                        }
                    });

                    chunkIndex++;
                }

                currentChunk.Clear();
                chunkConfidences.Clear();
                if (!isLast)
                    chunkStartSec = segments[i + 1].StartSeconds;
            }
        }

        return items;
    }

    private static string BuildMetadataContent(Video video)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {video.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Channel:** {video.Author.ChannelTitle}");
        sb.AppendLine($"**Duration:** {video.Duration}");
        sb.AppendLine($"**Uploaded:** {video.UploadDate:yyyy-MM-dd}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(video.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(video.Description);
        }
        return sb.ToString();
    }

    private static string FormatTimestamp(TimeSpan ts)
        => ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";

    private static string SanitizeSourceTag(string input)
        => Regex.Replace(input.ToLowerInvariant(), @"[^a-z0-9_-]", "-").Trim('-');
}

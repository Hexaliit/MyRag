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
public sealed partial class YouTubeExtractor
{
    [GeneratedRegex(@"[^a-z0-9_-]")]
    private static partial Regex SourceTagSanitizeRegex();

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

            var ext = audioStream.Container.Name;
            tempPath = Path.Combine(Path.GetTempPath(), $"yt-{videoId.Value}.{ext}");
            progress?.Invoke($"Downloading audio ({audioStream.Bitrate.MegaBitsPerSecond:F1} Mbps, {audioStream.Container.Name})...");
            await _youtube.Videos.Streams.DownloadAsync(audioStream, tempPath, cancellationToken: ct);

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

        // Query directly against manifest.Tracks (IReadOnlyList) — no .ToList()
        var tracks = manifest.Tracks;

        ClosedCaptionTrackInfo? autoEnglish = null, manualAny = null;
        ClosedCaptionTrackInfo? first = null;

        foreach (var t in tracks)
        {
            first ??= t;
            var isEnglish = t.Language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase);

            if (isEnglish && !t.IsAutoGenerated) return t; // Best: manual English
            if (isEnglish && t.IsAutoGenerated) autoEnglish ??= t;
            if (!t.IsAutoGenerated) manualAny ??= t;
        }

        return autoEnglish ?? manualAny ?? first;
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
                        ? string.Concat(video.Description.AsSpan(0, 300), "...")
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

        if (descriptionChapters.Count >= 2)
        {
            progress?.Invoke($"Chunking by {descriptionChapters.Count} description chapters");
            return ChunkByDescriptionChapters(video, captions, descriptionChapters, sourceTag);
        }

        progress?.Invoke("Chunking by subtitle gaps");
        return ChunkByGaps(video, captions, sourceTag);
    }

    /// <summary>
    /// Single-pass O(n) chapter chunking. Captions are sorted by offset, so we walk
    /// through both captions and chapters in parallel with two pointers.
    /// </summary>
    private static List<ContentItem> ChunkByDescriptionChapters(
        Video video,
        List<ClosedCaption> captions,
        List<DescriptionChapter> chapters,
        string sourceTag)
    {
        var items = new List<ContentItem>(chapters.Count);
        var videoDuration = video.Duration ?? TimeSpan.MaxValue;
        var captionIndex = 0;
        var sb = new StringBuilder(4096);

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var nextStart = i + 1 < chapters.Count ? chapters[i + 1].Timestamp : videoDuration;

            sb.Clear();

            // Advance caption pointer to this chapter's start
            while (captionIndex < captions.Count && captions[captionIndex].Offset < chapter.Timestamp)
                captionIndex++;

            // Collect captions within this chapter's range
            var chapterCaptionStart = captionIndex;
            while (captionIndex < captions.Count && captions[captionIndex].Offset < nextStart)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(captions[captionIndex].Text.Trim());
                captionIndex++;
            }

            if (sb.Length == 0) continue;

            var text = sb.ToString();
            var timestamp = FormatTimestamp(chapter.Timestamp);

            items.Add(new ContentItem
            {
                Id = $"yt:{video.Id}:ch{i}",
                Source = sourceTag,
                Title = $"{video.Title} - [{timestamp}] {chapter.Title}",
                Url = $"{video.Url}&t={(int)chapter.Timestamp.TotalSeconds}",
                Content = text,
                Summary = text.Length > 300 ? string.Concat(text.AsSpan(0, 300), "...") : text,
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

            // Reset pointer for next chapter (captions already advanced)
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
        var currentChunk = new StringBuilder(4096);
        var chunkStart = captions[0].Offset;
        var chunkIndex = 0;

        for (var i = 0; i < captions.Count; i++)
        {
            var caption = captions[i];
            if (currentChunk.Length > 0) currentChunk.Append(' ');
            currentChunk.Append(caption.Text.AsSpan().Trim());

            var isLast = i == captions.Count - 1;
            var hasGap = !isLast &&
                (captions[i + 1].Offset - (caption.Offset + caption.Duration)) >= gapThreshold;

            if ((hasGap && currentChunk.Length > 100) || isLast)
            {
                var text = currentChunk.ToString().Trim();
                if (text.Length > 0)
                {
                    var timestamp = FormatTimestamp(chunkStart);
                    var title = text.Length > 60 ? string.Concat(text.AsSpan(0, 57), "...") : text;

                    items.Add(new ContentItem
                    {
                        Id = $"yt:{video.Id}:s{chunkIndex}",
                        Source = sourceTag,
                        Title = $"{video.Title} - [{timestamp}] {title}",
                        Url = $"{video.Url}&t={(int)chunkStart.TotalSeconds}",
                        Content = text,
                        Summary = text.Length > 300 ? string.Concat(text.AsSpan(0, 300), "...") : text,
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
    /// </summary>
    private static List<ContentItem> BuildContentItemsFromTranscript(
        Video video,
        List<TranscriptSegmentInfo> segments,
        List<DescriptionChapter> descriptionChapters,
        string sourceTag,
        Action<string>? progress)
    {
        if (descriptionChapters.Count >= 2)
        {
            progress?.Invoke($"Chunking transcription by {descriptionChapters.Count} description chapters");
            return ChunkTranscriptByChapters(video, segments, descriptionChapters, sourceTag);
        }

        progress?.Invoke("Chunking transcription by timing gaps");
        return ChunkTranscriptByGaps(video, segments, sourceTag);
    }

    /// <summary>
    /// Single-pass O(n) transcript chapter chunking with two-pointer scan.
    /// </summary>
    private static List<ContentItem> ChunkTranscriptByChapters(
        Video video,
        List<TranscriptSegmentInfo> segments,
        List<DescriptionChapter> chapters,
        string sourceTag)
    {
        var items = new List<ContentItem>(chapters.Count);
        var videoDurationSec = (video.Duration ?? TimeSpan.MaxValue).TotalSeconds;
        var segIndex = 0;
        var sb = new StringBuilder(4096);

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var chapterStartSec = chapter.Timestamp.TotalSeconds;
            var nextStartSec = i + 1 < chapters.Count
                ? chapters[i + 1].Timestamp.TotalSeconds
                : videoDurationSec;

            sb.Clear();
            double confidenceSum = 0;
            var segCount = 0;

            // Advance segment pointer to this chapter's start
            while (segIndex < segments.Count && segments[segIndex].StartSeconds < chapterStartSec)
                segIndex++;

            // Collect segments within this chapter's range
            while (segIndex < segments.Count && segments[segIndex].StartSeconds < nextStartSec)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(segments[segIndex].Text.AsSpan().Trim());
                confidenceSum += segments[segIndex].Confidence;
                segCount++;
                segIndex++;
            }

            if (sb.Length == 0) continue;

            var text = sb.ToString();
            var timestamp = FormatTimestamp(chapter.Timestamp);
            var avgConfidence = segCount > 0 ? confidenceSum / segCount : 0.0;

            items.Add(new ContentItem
            {
                Id = $"yt:{video.Id}:tch{i}",
                Source = sourceTag,
                Title = $"{video.Title} - [{timestamp}] {chapter.Title}",
                Url = $"{video.Url}&t={(int)chapter.Timestamp.TotalSeconds}",
                Content = text,
                Summary = text.Length > 300 ? string.Concat(text.AsSpan(0, 300), "...") : text,
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
        var currentChunk = new StringBuilder(4096);
        var chunkStartSec = segments[0].StartSeconds;
        var chunkIndex = 0;
        double confidenceSum = 0;
        var confidenceCount = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (currentChunk.Length > 0) currentChunk.Append(' ');
            currentChunk.Append(seg.Text.AsSpan().Trim());
            confidenceSum += seg.Confidence;
            confidenceCount++;

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
                    var title = text.Length > 60 ? string.Concat(text.AsSpan(0, 57), "...") : text;
                    var avgConfidence = confidenceCount > 0 ? confidenceSum / confidenceCount : 0.0;

                    items.Add(new ContentItem
                    {
                        Id = $"yt:{video.Id}:ts{chunkIndex}",
                        Source = sourceTag,
                        Title = $"{video.Title} - [{timestamp}] {title}",
                        Url = $"{video.Url}&t={(int)chunkStartSec}",
                        Content = text,
                        Summary = text.Length > 300 ? string.Concat(text.AsSpan(0, 300), "...") : text,
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
                confidenceSum = 0;
                confidenceCount = 0;
                if (!isLast)
                    chunkStartSec = segments[i + 1].StartSeconds;
            }
        }

        return items;
    }

    private static string BuildMetadataContent(Video video)
    {
        var sb = new StringBuilder(512);
        sb.Append("# ").AppendLine(video.Title);
        sb.AppendLine();
        sb.Append("**Channel:** ").AppendLine(video.Author.ChannelTitle);
        sb.Append("**Duration:** ").Append(video.Duration).AppendLine();
        sb.Append("**Uploaded:** ").Append(video.UploadDate.ToString("yyyy-MM-dd")).AppendLine();
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
        => SourceTagSanitizeRegex().Replace(input.ToLowerInvariant(), "-").Trim('-');
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Waves;

namespace VideoSummarizer.Core.Services;

/// <summary>
/// Generates and manages video processing artifacts (frames, text files, audio tracks).
/// Creates evidence files that can be indexed by DocSummarizer and stored for retrieval.
/// </summary>
public class ArtifactGenerationService
{
    private readonly ILogger<ArtifactGenerationService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ArtifactGenerationService(ILogger<ArtifactGenerationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generate all configured artifacts for a video.
    /// </summary>
    public async Task<VideoArtifactCollection> GenerateArtifactsAsync(
        VideoContext context,
        ArtifactGenerationOptions options,
        CancellationToken ct = default)
    {
        var videoId = context.Metadata?.Id ?? Guid.NewGuid();
        var collection = new VideoArtifactCollection
        {
            VideoId = videoId,
            BasePath = options.OutputDirectory
        };

        // Ensure output directory exists
        var outputDir = GetOutputDirectory(context.VideoPath, options);
        Directory.CreateDirectory(outputDir);

        // Generate various artifact types
        if (options.ExtractSceneFrames)
            await GenerateSceneFramesAsync(context, collection, outputDir, options, ct);

        if (options.ExportTranscript)
            await GenerateTranscriptAsync(context, collection, outputDir, ct);

        if (options.GenerateSubtitles)
            await GenerateSubtitlesAsync(context, collection, outputDir, ct);

        if (options.ExportOcr)
            await GenerateOcrTextAsync(context, collection, outputDir, ct);

        if (options.GenerateMarkdown)
            await GenerateMarkdownDocumentAsync(context, collection, outputDir, ct);

        _logger.LogInformation("Generated {Count} artifacts for video {VideoId}",
            collection.Artifacts.Count, videoId);

        return collection;
    }

    /// <summary>
    /// Generate low-res scene frame thumbnails.
    /// </summary>
    private async Task GenerateSceneFramesAsync(
        VideoContext context,
        VideoArtifactCollection collection,
        string outputDir,
        ArtifactGenerationOptions options,
        CancellationToken ct)
    {
        var scenes = context.GetCached<List<SceneSegment>>("scenes");
        var keyframePaths = context.GetCached<Dictionary<int, string>>("keyframe_paths");

        if (scenes == null || keyframePaths == null)
        {
            _logger.LogDebug("No scenes or keyframes available for scene frame generation");
            return;
        }

        var frameDir = Path.Combine(outputDir, "scene_frames");
        Directory.CreateDirectory(frameDir);

        foreach (var scene in scenes)
        {
            ct.ThrowIfCancellationRequested();

            // Get keyframe for this scene
            var keyframeIndex = scene.KeyframeIndices.FirstOrDefault();
            if (!keyframePaths.TryGetValue(keyframeIndex, out var keyframePath))
                continue;

            if (!File.Exists(keyframePath))
                continue;

            // Create thumbnail (in real implementation, would resize with ImageSharp/SkiaSharp)
            var thumbPath = Path.Combine(frameDir, $"scene_{scene.Id:N}_thumb.jpg");

            // For now, copy the keyframe (actual resize would happen here)
            File.Copy(keyframePath, thumbPath, overwrite: true);

            var fileInfo = new FileInfo(thumbPath);
            collection.Add(new VideoArtifact
            {
                Id = Guid.NewGuid(),
                VideoId = collection.VideoId,
                Type = VideoArtifactType.SceneFrame,
                FilePath = thumbPath,
                FileSize = fileInfo.Length,
                MimeType = "image/jpeg",
                StartTime = scene.StartTime,
                EndTime = scene.EndTime,
                RelatedEntityId = scene.Id,
                Resolution = options.SceneFrameResolution,
                Quality = ArtifactQuality.Standard,
                Metadata = new Dictionary<string, object?>
                {
                    ["scene_label"] = scene.Label,
                    ["keyframe_index"] = keyframeIndex
                }
            });
        }

        _logger.LogInformation("Generated {Count} scene frames", collection.OfType(VideoArtifactType.SceneFrame).Count());
        await Task.CompletedTask;
    }

    /// <summary>
    /// Generate transcript text file from utterances.
    /// </summary>
    private async Task GenerateTranscriptAsync(
        VideoContext context,
        VideoArtifactCollection collection,
        string outputDir,
        CancellationToken ct)
    {
        if (context.Utterances.Count == 0)
        {
            _logger.LogDebug("No utterances available for transcript generation");
            return;
        }

        // Plain text transcript
        var txtPath = Path.Combine(outputDir, "transcript.txt");
        var sb = new StringBuilder();

        foreach (var utterance in context.Utterances.OrderBy(u => u.StartTime))
        {
            var timestamp = FormatTimestamp(utterance.StartTime);
            if (!string.IsNullOrEmpty(utterance.SpeakerId))
            {
                sb.AppendLine($"[{timestamp}] {utterance.SpeakerId}:");
            }
            else
            {
                sb.Append($"[{timestamp}] ");
            }
            sb.AppendLine(utterance.Text);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(txtPath, sb.ToString(), ct);
        var fileInfo = new FileInfo(txtPath);

        collection.Add(new VideoArtifact
        {
            Id = Guid.NewGuid(),
            VideoId = collection.VideoId,
            Type = VideoArtifactType.TranscriptTxt,
            FilePath = txtPath,
            FileSize = fileInfo.Length,
            MimeType = "text/plain",
            Language = context.Utterances.FirstOrDefault()?.Language,
            Metadata = new Dictionary<string, object?>
            {
                ["utterance_count"] = context.Utterances.Count,
                ["speaker_count"] = context.Utterances.Select(u => u.SpeakerId).Distinct().Count()
            }
        });

        // JSON transcript with timestamps
        var jsonPath = Path.Combine(outputDir, "transcript.json");
        var transcriptData = context.Utterances
            .OrderBy(u => u.StartTime)
            .Select(u => new
            {
                start = u.StartTime,
                end = u.EndTime,
                speaker = u.SpeakerId,
                text = u.Text,
                confidence = u.Confidence
            })
            .ToList();

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(transcriptData, JsonOptions), ct);
        var jsonFileInfo = new FileInfo(jsonPath);

        collection.Add(new VideoArtifact
        {
            Id = Guid.NewGuid(),
            VideoId = collection.VideoId,
            Type = VideoArtifactType.TranscriptJson,
            FilePath = jsonPath,
            FileSize = jsonFileInfo.Length,
            MimeType = "application/json",
            Language = context.Utterances.FirstOrDefault()?.Language
        });

        _logger.LogInformation("Generated transcript files (txt + json)");
    }

    /// <summary>
    /// Generate SRT subtitle file from utterances.
    /// </summary>
    private async Task GenerateSubtitlesAsync(
        VideoContext context,
        VideoArtifactCollection collection,
        string outputDir,
        CancellationToken ct)
    {
        if (context.Utterances.Count == 0)
            return;

        var srtPath = Path.Combine(outputDir, "subtitles.srt");
        var sb = new StringBuilder();
        var index = 1;

        foreach (var utterance in context.Utterances.OrderBy(u => u.StartTime))
        {
            sb.AppendLine(index.ToString());
            sb.AppendLine($"{FormatSrtTimestamp(utterance.StartTime)} --> {FormatSrtTimestamp(utterance.EndTime)}");
            sb.AppendLine(utterance.Text);
            sb.AppendLine();
            index++;
        }

        await File.WriteAllTextAsync(srtPath, sb.ToString(), ct);
        var fileInfo = new FileInfo(srtPath);

        collection.Add(new VideoArtifact
        {
            Id = Guid.NewGuid(),
            VideoId = collection.VideoId,
            Type = VideoArtifactType.SubtitleSrt,
            FilePath = srtPath,
            FileSize = fileInfo.Length,
            MimeType = "application/x-subrip",
            Language = context.Utterances.FirstOrDefault()?.Language
        });

        _logger.LogInformation("Generated SRT subtitle file with {Count} entries", context.Utterances.Count);
    }

    /// <summary>
    /// Generate OCR text file from text tracks.
    /// </summary>
    private async Task GenerateOcrTextAsync(
        VideoContext context,
        VideoArtifactCollection collection,
        string outputDir,
        CancellationToken ct)
    {
        var textTracks = context.GetCached<List<TextTrack>>("text_tracks");
        if (textTracks == null || textTracks.Count == 0)
        {
            _logger.LogDebug("No text tracks available for OCR export");
            return;
        }

        var txtPath = Path.Combine(outputDir, "ocr_text.txt");
        var sb = new StringBuilder();

        sb.AppendLine("# On-Screen Text (OCR)");
        sb.AppendLine();

        foreach (var track in textTracks.OrderBy(t => t.StartTime))
        {
            var timeRange = $"{FormatTimestamp(track.StartTime)} - {FormatTimestamp(track.EndTime)}";
            sb.AppendLine($"[{timeRange}] ({track.TextType})");
            sb.AppendLine(track.Text);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(txtPath, sb.ToString(), ct);
        var fileInfo = new FileInfo(txtPath);

        collection.Add(new VideoArtifact
        {
            Id = Guid.NewGuid(),
            VideoId = collection.VideoId,
            Type = VideoArtifactType.OcrText,
            FilePath = txtPath,
            FileSize = fileInfo.Length,
            MimeType = "text/plain",
            Metadata = new Dictionary<string, object?>
            {
                ["text_track_count"] = textTracks.Count,
                ["unique_texts"] = textTracks.Select(t => t.CanonicalText ?? t.Text).Distinct().Count()
            }
        });

        _logger.LogInformation("Generated OCR text file with {Count} text tracks", textTracks.Count);
    }

    /// <summary>
    /// Generate markdown document for DocSummarizer integration.
    /// Combines all video content into a structured document.
    /// </summary>
    private async Task GenerateMarkdownDocumentAsync(
        VideoContext context,
        VideoArtifactCollection collection,
        string outputDir,
        CancellationToken ct)
    {
        var videoId = context.Metadata?.Id ?? Guid.NewGuid();
        var title = Path.GetFileNameWithoutExtension(context.VideoPath);

        // Get parsed title if available
        var parsedInfo = context.GetCached<ParsedMediaInfo>("parsed_filename");
        if (parsedInfo?.Title != null)
            title = parsedInfo.Title;

        // Get external metadata
        var externalMeta = context.GetCached<ExternalMediaMetadata>("external_metadata");
        if (externalMeta?.Title != null)
            title = externalMeta.Title;

        var doc = new VideoMarkdownDocument
        {
            VideoId = videoId,
            Title = title,
            Description = externalMeta?.Overview,
            Duration = TimeSpan.FromSeconds(context.Metadata?.Duration ?? 0),
            Metadata = new VideoMetadataSection
            {
                Format = context.Metadata?.VideoCodec,
                Resolution = context.Metadata != null
                    ? $"{context.Metadata.Width}x{context.Metadata.Height}"
                    : null,
                Speakers = context.GetCached<List<Speaker>>("speakers")?.Select(s => s.Name ?? s.Id).ToList(),
                Language = context.Utterances.FirstOrDefault()?.Language
            }
        };

        // Build scenes
        var scenes = context.GetCached<List<SceneSegment>>("scenes");
        var textTracks = context.GetCached<List<TextTrack>>("text_tracks");

        if (scenes != null)
        {
            foreach (var scene in scenes.OrderBy(s => s.StartTime))
            {
                var sceneUtterances = context.Utterances
                    .Where(u => u.StartTime >= scene.StartTime && u.StartTime <= scene.EndTime)
                    .OrderBy(u => u.StartTime)
                    .ToList();

                var sceneOcr = textTracks?
                    .Where(t => t.StartTime >= scene.StartTime && t.StartTime <= scene.EndTime)
                    .Select(t => t.Text)
                    .Distinct()
                    .ToList();

                doc.Scenes.Add(new SceneSection
                {
                    Index = scenes.IndexOf(scene),
                    Label = scene.Label,
                    StartTime = scene.StartTime,
                    EndTime = scene.EndTime,
                    TranscriptExcerpt = string.Join(" ", sceneUtterances.Take(3).Select(u => u.Text)),
                    OcrText = sceneOcr
                });
            }
        }

        // Build transcript section
        if (context.Utterances.Count > 0)
        {
            var speakers = context.Utterances
                .Select(u => u.SpeakerId)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToList();

            if (speakers.Count > 0)
            {
                doc.Transcript = new TranscriptSection
                {
                    Language = context.Utterances.FirstOrDefault()?.Language,
                    SpeakerSegments = context.Utterances
                        .OrderBy(u => u.StartTime)
                        .Select(u => new SpeakerSegmentSection
                        {
                            Speaker = u.SpeakerId ?? "Unknown",
                            StartTime = u.StartTime,
                            Text = u.Text
                        })
                        .ToList()
                };
            }
            else
            {
                doc.Transcript = new TranscriptSection
                {
                    Language = context.Utterances.FirstOrDefault()?.Language,
                    FullText = string.Join(" ", context.Utterances.OrderBy(u => u.StartTime).Select(u => u.Text))
                };
            }
        }

        // Add entity sections from face tracking and cast
        var faceTracks = context.GetCached<List<FaceTrack>>("face_tracks");
        var castMembers = context.GetCached<List<CastMember>>("cast_members");

        if (faceTracks?.Count > 0 || castMembers?.Count > 0)
        {
            // Add people from face tracks
            if (faceTracks != null)
            {
                foreach (var faceTrack in faceTracks.GroupBy(f => f.FaceIdentityId).Take(10))
                {
                    var tracks = faceTrack.ToList();
                    doc.Entities.Add(new EntitySection
                    {
                        Type = "person",
                        Name = $"Person {faceTrack.Key.ToString()[..8]}",
                        Description = $"Appears in {tracks.Count} scene(s)",
                        AppearanceTimes = tracks.Select(t => t.StartTime).ToList()
                    });
                }
            }

            // Add people from cast
            if (castMembers != null)
            {
                foreach (var cast in castMembers.Take(10))
                {
                    doc.Entities.Add(new EntitySection
                    {
                        Type = "person",
                        Name = cast.Name,
                        Description = $"as {cast.Character}"
                    });
                }
            }
        }

        // Write markdown file
        var mdPath = Path.Combine(outputDir, "video_document.md");
        var markdown = doc.ToMarkdown();
        await File.WriteAllTextAsync(mdPath, markdown, ct);
        var fileInfo = new FileInfo(mdPath);

        collection.Add(new VideoArtifact
        {
            Id = Guid.NewGuid(),
            VideoId = collection.VideoId,
            Type = VideoArtifactType.MarkdownDocument,
            FilePath = mdPath,
            FileSize = fileInfo.Length,
            MimeType = "text/markdown",
            ContentHash = ComputeHash(markdown),
            Metadata = new Dictionary<string, object?>
            {
                ["scene_count"] = doc.Scenes.Count,
                ["entity_count"] = doc.Entities.Count,
                ["has_transcript"] = doc.Transcript != null
            }
        });

        _logger.LogInformation("Generated markdown document for DocSummarizer ({Size} bytes)", fileInfo.Length);
    }

    /// <summary>
    /// Get output directory for artifacts.
    /// </summary>
    private static string GetOutputDirectory(string videoPath, ArtifactGenerationOptions options)
    {
        if (!string.IsNullOrEmpty(options.OutputDirectory))
            return options.OutputDirectory;

        var videoDir = Path.GetDirectoryName(videoPath) ?? ".";
        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(videoDir, $".{videoName}_artifacts");
    }

    private static string FormatTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private static string FormatSrtTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"hh\:mm\:ss\,fff");
    }

    private static string ComputeHash(string content)
    {
        var hash = System.IO.Hashing.XxHash64.Hash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }
}

using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Pipeline;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Waves;

namespace VideoSummarizer.Core.Pipeline;

/// <summary>
/// Pipeline implementation for video files.
/// Chains to ImageSummarizer for keyframe analysis and AudioSummarizer for transcription.
/// Produces shots, scenes, utterances, and text tracks as evidence for RAG.
/// </summary>
public class VideoPipeline : PipelineBase
{
    private readonly VideoWaveCoordinator _coordinator;
    private readonly ILogger<VideoPipeline> _logger;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpeg", ".mpg"
    };

    public VideoPipeline(
        VideoWaveCoordinator coordinator,
        ILogger<VideoPipeline> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string PipelineId => "video";

    /// <inheritdoc />
    public override string Name => "Video Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => VideoExtensions;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing video: {FilePath}", filePath);

        var workingDir = Path.Combine(Path.GetTempPath(), "VideoSummarizer", Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);

        try
        {
            // Create video context
            var context = new VideoContext
            {
                VideoPath = filePath,
                WorkingDirectory = workingDir,
                OnProgress = (stage, percent) =>
                {
                    progress?.Report(new PipelineProgress(stage, $"Processing video: {stage}", percent));
                }
            };

            progress?.Report(new PipelineProgress("Initializing", "Starting video analysis", 0));

            // Run all waves via coordinator
            await _coordinator.ProcessAsync(context, ct);

            progress?.Report(new PipelineProgress("Extracting", "Building content chunks", 95));

            // Convert video signals to content chunks for RAG
            var chunks = BuildContentChunks(context, filePath);

            _logger.LogInformation("Processed video: {Shots} shots, {Scenes} scenes, {Utterances} utterances, {Chunks} chunks",
                context.Shots.Count,
                context.Scenes.Count,
                context.Utterances.Count,
                chunks.Count);

            return chunks;
        }
        finally
        {
            // Cleanup working directory
            try
            {
                if (Directory.Exists(workingDir))
                {
                    Directory.Delete(workingDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup working directory: {Path}", workingDir);
            }
        }
    }

    /// <summary>
    /// Convert video context to content chunks for RAG indexing.
    /// Creates chunks for: scenes, transcripts, text tracks, and metadata.
    /// </summary>
    private List<ContentChunk> BuildContentChunks(VideoContext context, string filePath)
    {
        var chunks = new List<ContentChunk>();
        var chunkIndex = 0;

        // 1. Scene-based chunks (best for video retrieval)
        foreach (var scene in context.Scenes)
        {
            var sceneText = BuildSceneText(context, scene);
            if (string.IsNullOrWhiteSpace(sceneText)) continue;

            var embedding = context.GetCached<float[]>($"scene_centroid.{scene.Id}");

            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = sceneText,
                ContentType = ContentType.Summary, // Video scenes are summaries of content
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(sceneText),
                Confidence = scene.Confidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "video_scene",
                    ["scene_id"] = scene.Id,
                    ["shot_count"] = scene.ShotIds.Count,
                    ["label"] = scene.Label,
                    ["key_terms"] = scene.KeyTerms,
                    ["speakers"] = scene.SpeakerIds,
                    ["start_time"] = scene.StartTime,
                    ["end_time"] = scene.EndTime,
                    ["embedding"] = embedding
                }
            });
        }

        // 2. Transcript chunks (grouped by time windows)
        var transcriptChunks = BuildTranscriptChunks(context, filePath, ref chunkIndex);
        chunks.AddRange(transcriptChunks);

        // 3. Text track chunks (on-screen text)
        foreach (var textTrack in context.TextTracks)
        {
            if (string.IsNullOrWhiteSpace(textTrack.Text)) continue;

            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = $"On-screen text: {textTrack.Text}",
                ContentType = ContentType.ImageOcr,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(textTrack.Text),
                Confidence = textTrack.Confidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "video_ocr",
                    ["text_type"] = textTrack.TextType.ToString(),
                    ["text_track_id"] = textTrack.Id,
                    ["start_time"] = textTrack.StartTime,
                    ["end_time"] = textTrack.EndTime
                }
            });
        }

        // 4. Video metadata chunk
        if (context.Metadata != null)
        {
            var metadataText = BuildMetadataText(context);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = metadataText,
                ContentType = ContentType.Summary,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(metadataText),
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "video_metadata",
                    ["duration"] = context.Metadata.Duration,
                    ["resolution"] = $"{context.Metadata.Width}x{context.Metadata.Height}",
                    ["fps"] = context.Metadata.Fps,
                    ["shot_count"] = context.Shots.Count,
                    ["scene_count"] = context.Scenes.Count
                }
            });
        }

        return chunks;
    }

    /// <summary>
    /// Build text representation of a scene for RAG.
    /// </summary>
    private string BuildSceneText(VideoContext context, SceneSegment scene)
    {
        var parts = new List<string>();

        // Add label if available
        if (!string.IsNullOrEmpty(scene.Label))
        {
            parts.Add($"Scene: {scene.Label}");
        }

        // Add time range
        parts.Add($"[{FormatTime(scene.StartTime)} - {FormatTime(scene.EndTime)}]");

        // Add key terms
        if (scene.KeyTerms.Count > 0)
        {
            parts.Add($"Topics: {string.Join(", ", scene.KeyTerms)}");
        }

        // Add transcript from utterances in this scene
        var sceneUtterances = context.Utterances
            .Where(u => u.StartTime >= scene.StartTime && u.EndTime <= scene.EndTime)
            .OrderBy(u => u.StartTime)
            .ToList();

        if (sceneUtterances.Count > 0)
        {
            var transcript = string.Join(" ", sceneUtterances.Select(u => u.Text));
            parts.Add($"Speech: {transcript}");
        }

        // Add on-screen text
        var sceneTextTracks = context.TextTracks
            .Where(t => t.StartTime < scene.EndTime && t.EndTime > scene.StartTime)
            .Select(t => t.Text)
            .Distinct()
            .ToList();

        if (sceneTextTracks.Count > 0)
        {
            parts.Add($"On-screen: {string.Join("; ", sceneTextTracks)}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Build transcript chunks grouped by time windows.
    /// </summary>
    private List<ContentChunk> BuildTranscriptChunks(
        VideoContext context,
        string filePath,
        ref int chunkIndex)
    {
        var chunks = new List<ContentChunk>();
        var windowSize = 60.0; // 1-minute windows

        // Group utterances by time window
        var utteranceGroups = context.Utterances
            .GroupBy(u => (int)(u.StartTime / windowSize))
            .OrderBy(g => g.Key);

        foreach (var group in utteranceGroups)
        {
            var utterances = group.OrderBy(u => u.StartTime).ToList();
            if (utterances.Count == 0) continue;

            var text = string.Join(" ", utterances.Select(u => u.Text));
            var startTime = utterances.Min(u => u.StartTime);
            var endTime = utterances.Max(u => u.EndTime);
            var avgConfidence = utterances.Average(u => u.Confidence);

            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = text,
                ContentType = ContentType.Transcript,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(text),
                Confidence = avgConfidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "video_transcript",
                    ["utterance_count"] = utterances.Count,
                    ["time_window"] = $"{FormatTime(startTime)} - {FormatTime(endTime)}",
                    ["start_time"] = startTime,
                    ["end_time"] = endTime
                }
            });
        }

        return chunks;
    }

    /// <summary>
    /// Build metadata text summary.
    /// </summary>
    private static string BuildMetadataText(VideoContext context)
    {
        var meta = context.Metadata!;
        var parts = new List<string>
        {
            $"Video duration: {FormatTime(meta.Duration)}",
            $"Resolution: {meta.Width}x{meta.Height} @ {meta.Fps:F1} fps",
            $"Codec: {meta.VideoCodec}"
        };

        if (!string.IsNullOrEmpty(meta.AudioCodec))
        {
            parts.Add($"Audio: {meta.AudioCodec} ({meta.AudioChannels} channels, {meta.AudioSampleRate}Hz)");
        }

        parts.Add($"Shots: {context.Shots.Count}, Scenes: {context.Scenes.Count}");

        if (context.Utterances.Count > 0)
        {
            var wordCount = context.Utterances.Sum(u =>
                u.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            parts.Add($"Transcript: {context.Utterances.Count} utterances, ~{wordCount} words");
        }

        if (context.TextTracks.Count > 0)
        {
            parts.Add($"On-screen text: {context.TextTracks.Count} tracked elements");
        }

        return string.Join("\n", parts);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}

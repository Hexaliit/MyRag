using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Stage 5: Evidence generation for RAG retrieval.
/// Creates VideoEvidence pointers with embeddings for semantic search.
/// Each evidence points to a specific moment in the video with context.
/// </summary>
public class EvidenceGenerationWave : IVideoWave
{
    private readonly ILogger<EvidenceGenerationWave> _logger;

    public string Name => "evidence_generation";
    public int Priority => 300; // After scene clustering
    public IReadOnlyList<string> Tags => [VideoSignalTags.Scene, VideoSignalTags.Shot];

    public EvidenceGenerationWave(ILogger<EvidenceGenerationWave> logger)
    {
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        context.Metadata != null &&
        (context.Scenes.Count > 0 || context.Shots.Count > 0 || context.Utterances.Count > 0);

    public Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Generating evidence", 0);

        var videoId = context.Metadata!.Id;

        // 1. Create evidence from scenes (highest level, best for retrieval)
        context.ReportProgress("Creating scene evidence", 20);
        foreach (var scene in context.Scenes)
        {
            var embedding = context.GetCached<float[]>($"scene_centroid.{scene.Id}");

            // Get representative keyframe from scene
            var keyframeIndex = scene.KeyframeIndices.FirstOrDefault();
            var keyframePath = keyframeIndex > 0
                ? context.Keyframes.GetValueOrDefault(keyframeIndex)
                : null;

            // Build text content from scene
            var textContent = BuildSceneTextContent(context, scene);

            context.Evidence.Add(new VideoEvidence
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                Type = VideoEvidenceType.Scene,
                StartTime = scene.StartTime,
                EndTime = scene.EndTime,
                FrameIndex = keyframeIndex > 0 ? keyframeIndex : null,
                KeyframePath = keyframePath,
                TextContent = textContent,
                Embedding = embedding,
                RelevanceScore = scene.Confidence,
                Provenance = $"VideoSummarizer/{Name}"
            });
        }

        // 2. Create evidence from shots (for precise navigation)
        context.ReportProgress("Creating shot evidence", 40);
        foreach (var shot in context.Shots)
        {
            var embedding = context.KeyframeEmbeddings.GetValueOrDefault(shot.KeyframeIndex);
            var keyframePath = context.Keyframes.GetValueOrDefault(shot.KeyframeIndex);

            // Get caption for this keyframe if available
            var caption = context.GetCached<string>($"caption.{shot.KeyframeIndex}");

            context.Evidence.Add(new VideoEvidence
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                Type = VideoEvidenceType.Shot,
                StartTime = shot.StartTime,
                EndTime = shot.EndTime,
                FrameIndex = shot.KeyframeIndex,
                KeyframePath = keyframePath,
                TextContent = caption,
                Embedding = embedding,
                RelevanceScore = shot.Confidence,
                Provenance = $"VideoSummarizer/{Name}"
            });
        }

        // 3. Create evidence from keyframes (for visual search)
        context.ReportProgress("Creating keyframe evidence", 55);
        foreach (var (frameIndex, embedding) in context.KeyframeEmbeddings)
        {
            // Skip if we already have a shot for this frame
            if (context.Evidence.Any(e => e.Type == VideoEvidenceType.Shot && e.FrameIndex == frameIndex))
                continue;

            var timestamp = context.FrameTimestamps.GetValueOrDefault(frameIndex, 0);
            var keyframePath = context.Keyframes.GetValueOrDefault(frameIndex);
            var caption = context.GetCached<string>($"caption.{frameIndex}");

            context.Evidence.Add(new VideoEvidence
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                Type = VideoEvidenceType.Keyframe,
                StartTime = timestamp,
                EndTime = timestamp + 0.1, // Single frame
                FrameIndex = frameIndex,
                KeyframePath = keyframePath,
                TextContent = caption,
                Embedding = embedding,
                RelevanceScore = 0.8,
                Provenance = $"VideoSummarizer/{Name}"
            });
        }

        // 4. Create evidence from utterances (for speech search)
        context.ReportProgress("Creating utterance evidence", 70);
        foreach (var utterance in context.Utterances)
        {
            // Find keyframe closest to this utterance
            var midTime = (utterance.StartTime + utterance.EndTime) / 2;
            var closestFrame = context.FrameTimestamps
                .OrderBy(kv => Math.Abs(kv.Value - midTime))
                .FirstOrDefault();

            var embedding = closestFrame.Key > 0
                ? context.KeyframeEmbeddings.GetValueOrDefault(closestFrame.Key)
                : null;

            var keyframePath = closestFrame.Key > 0
                ? context.Keyframes.GetValueOrDefault(closestFrame.Key)
                : null;

            context.Evidence.Add(new VideoEvidence
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                Type = VideoEvidenceType.Utterance,
                StartTime = utterance.StartTime,
                EndTime = utterance.EndTime,
                FrameIndex = closestFrame.Key > 0 ? closestFrame.Key : null,
                KeyframePath = keyframePath,
                TextContent = utterance.Text,
                Embedding = embedding,
                RelevanceScore = utterance.Confidence,
                Provenance = $"VideoSummarizer/{Name}"
            });
        }

        // 5. Create evidence from text tracks (for OCR search)
        context.ReportProgress("Creating text track evidence", 85);
        foreach (var textTrack in context.TextTracks)
        {
            // Find keyframe in the text track's time range
            var midTime = (textTrack.StartTime + textTrack.EndTime) / 2;
            var closestFrame = context.FrameTimestamps
                .Where(kv => kv.Value >= textTrack.StartTime && kv.Value <= textTrack.EndTime)
                .OrderBy(kv => Math.Abs(kv.Value - midTime))
                .FirstOrDefault();

            var embedding = closestFrame.Key > 0
                ? context.KeyframeEmbeddings.GetValueOrDefault(closestFrame.Key)
                : null;

            var keyframePath = closestFrame.Key > 0
                ? context.Keyframes.GetValueOrDefault(closestFrame.Key)
                : null;

            context.Evidence.Add(new VideoEvidence
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                Type = VideoEvidenceType.TextTrack,
                StartTime = textTrack.StartTime,
                EndTime = textTrack.EndTime,
                FrameIndex = closestFrame.Key > 0 ? closestFrame.Key : null,
                KeyframePath = keyframePath,
                TextContent = textTrack.Text,
                Embedding = embedding,
                RelevanceScore = textTrack.Confidence,
                Provenance = $"VideoSummarizer/{Name}"
            });
        }

        // Add summary signals
        var evidenceByType = context.Evidence.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        var evidenceWithEmbeddings = context.Evidence.Count(e => e.Embedding != null);

        context.AddSignals([
            new VideoSignal
            {
                Key = "evidence.total_count",
                Value = context.Evidence.Count,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "evidence.with_embeddings",
                Value = evidenceWithEmbeddings,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "evidence.scene_count",
                Value = evidenceByType.GetValueOrDefault(VideoEvidenceType.Scene, 0),
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "evidence.shot_count",
                Value = evidenceByType.GetValueOrDefault(VideoEvidenceType.Shot, 0),
                Source = Name,
                Tags = [VideoSignalTags.Shot]
            },
            new VideoSignal
            {
                Key = "evidence.utterance_count",
                Value = evidenceByType.GetValueOrDefault(VideoEvidenceType.Utterance, 0),
                Source = Name,
                Tags = [VideoSignalTags.Speech]
            }
        ]);

        context.ReportProgress("Evidence generation complete", 100);

        _logger.LogInformation(
            "Generated {Total} evidence items ({WithEmbed} with embeddings): " +
            "{Scenes} scenes, {Shots} shots, {Utterances} utterances, {TextTracks} text tracks",
            context.Evidence.Count,
            evidenceWithEmbeddings,
            evidenceByType.GetValueOrDefault(VideoEvidenceType.Scene, 0),
            evidenceByType.GetValueOrDefault(VideoEvidenceType.Shot, 0),
            evidenceByType.GetValueOrDefault(VideoEvidenceType.Utterance, 0),
            evidenceByType.GetValueOrDefault(VideoEvidenceType.TextTrack, 0));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Build text content for a scene from utterances and text tracks.
    /// </summary>
    private static string BuildSceneTextContent(VideoContext context, SceneSegment scene)
    {
        var parts = new List<string>();

        // Add label if available
        if (!string.IsNullOrEmpty(scene.Label))
        {
            parts.Add($"Scene: {scene.Label}");
        }

        // Add key terms
        if (scene.KeyTerms.Count > 0)
        {
            parts.Add($"Topics: {string.Join(", ", scene.KeyTerms)}");
        }

        // Add utterances in this scene
        var utterances = context.Utterances
            .Where(u => u.StartTime >= scene.StartTime && u.EndTime <= scene.EndTime)
            .OrderBy(u => u.StartTime)
            .Select(u => u.Text)
            .ToList();

        if (utterances.Count > 0)
        {
            parts.Add($"Speech: {string.Join(" ", utterances)}");
        }

        // Add text tracks in this scene
        var textTracks = context.TextTracks
            .Where(t => t.StartTime < scene.EndTime && t.EndTime > scene.StartTime)
            .Select(t => t.Text)
            .Distinct()
            .ToList();

        if (textTracks.Count > 0)
        {
            parts.Add($"On-screen: {string.Join("; ", textTracks)}");
        }

        return string.Join("\n", parts);
    }
}

using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Wave that tracks faces across video frames and links them to the persistent face database.
///     Integrates with ImageSummarizer's face detection to build face tracks.
/// </summary>
public class FaceTrackingWave : IVideoWave
{
    private readonly FaceTrackingService _faceTracking;
    private readonly ILogger<FaceTrackingWave> _logger;

    public FaceTrackingWave(
        FaceTrackingService faceTracking,
        ILogger<FaceTrackingWave> logger)
    {
        _faceTracking = faceTracking;
        _logger = logger;
    }

    public string Name => "FaceTracking";
    public int Priority => 650; // After keyframe extraction, before transcription
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, "faces"];

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("Tracking faces across video: {Path}", context.VideoPath);

        var videoId = context.Metadata?.Id ?? Guid.NewGuid();
        var faceTracks = new List<FaceTrack>();
        var faceAppearances = new Dictionary<Guid, List<FaceAppearance>>();

        // Get face embeddings from keyframes (set by ImageAnalysisWave)
        var keyframeFaces = context.GetCached<Dictionary<int, List<FaceEmbeddingData>>>("keyframe_faces");

        if (keyframeFaces == null || keyframeFaces.Count == 0)
        {
            _logger.LogDebug("No face data found in keyframes");
            context.OnProgress?.Invoke("FaceTracking", 100);
            return;
        }

        context.OnProgress?.Invoke("FaceTracking", 10);

        // Get cast members for potential matching
        var castMembers = context.GetCached<List<CastMember>>("cast_members");

        // Process each keyframe's faces
        var processedFrames = 0;
        var totalFrames = keyframeFaces.Count;

        foreach (var (frameIndex, faces) in keyframeFaces.OrderBy(kv => kv.Key))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var face in faces)
            {
                // Match against persistent face database
                var matchResult = _faceTracking.MatchFace(
                    face.Embedding,
                    videoId,
                    new FaceMatchOptions
                    {
                        SimilarityThreshold = 0.75,
                        CreateNewIfNoMatch = true,
                        UpdateCentroidOnMatch = true
                    });

                if (matchResult.MatchedIdentity == null)
                    continue;

                var identityId = matchResult.MatchedIdentity.Id;

                // Create face appearance record
                var appearance = new FaceAppearance
                {
                    Id = Guid.NewGuid(),
                    VideoId = videoId,
                    FaceIdentityId = identityId,
                    FrameIndex = frameIndex,
                    Timestamp = GetFrameTimestamp(context, frameIndex),
                    BoundingBox = new FaceBoundingBox(
                        face.BoundingBox.X,
                        face.BoundingBox.Y,
                        face.BoundingBox.Width,
                        face.BoundingBox.Height),
                    Embedding = face.Embedding,
                    Confidence = face.Confidence
                };

                if (!faceAppearances.ContainsKey(identityId)) faceAppearances[identityId] = [];
                faceAppearances[identityId].Add(appearance);
            }

            processedFrames++;
            var progress = 10 + (int)(processedFrames / (double)totalFrames * 70);
            context.OnProgress?.Invoke("FaceTracking", progress);
        }

        context.OnProgress?.Invoke("FaceTracking", 80);

        // Build face tracks from appearances
        foreach (var (identityId, appearances) in faceAppearances)
        {
            // Group consecutive appearances into tracks
            var tracks = BuildTracksFromAppearances(appearances, videoId, identityId);
            faceTracks.AddRange(tracks);
        }

        // Try to match identities to cast members
        if (castMembers != null && castMembers.Count > 0) await TryMatchCastMembersAsync(faceTracks, castMembers, ct);

        // Store results
        context.SetCached("face_tracks", faceTracks);
        context.SetCached("face_appearances", faceAppearances.Values.SelectMany(x => x).ToList());

        // Add face statistics to metadata
        var identityStats = _faceTracking.GetStats();
        context.SetCached("face_stats", identityStats);

        _logger.LogInformation("Created {TrackCount} face tracks for {IdentityCount} identities",
            faceTracks.Count, faceAppearances.Count);

        context.OnProgress?.Invoke("FaceTracking", 100);
    }

    private static double GetFrameTimestamp(VideoContext context, int frameIndex)
    {
        // Try to get from frame timestamps cache
        var timestamps = context.GetCached<Dictionary<int, double>>("frame_timestamps");
        if (timestamps != null && timestamps.TryGetValue(frameIndex, out var ts)) return ts;

        // Estimate from FPS
        var fps = context.Metadata?.Fps ?? 30.0;
        return frameIndex / fps;
    }

    private List<FaceTrack> BuildTracksFromAppearances(
        List<FaceAppearance> appearances,
        Guid videoId,
        Guid identityId)
    {
        var tracks = new List<FaceTrack>();

        if (appearances.Count == 0)
            return tracks;

        // Sort by timestamp
        var sorted = appearances.OrderBy(a => a.Timestamp).ToList();

        // Group into tracks (gap > 5 seconds = new track)
        const double maxGap = 5.0;
        var currentTrack = new List<FaceAppearance> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i].Timestamp - sorted[i - 1].Timestamp;

            if (gap > maxGap)
            {
                // Close current track and start new one
                tracks.Add(CreateTrack(currentTrack, videoId, identityId));
                currentTrack = [];
            }

            currentTrack.Add(sorted[i]);
        }

        // Close final track
        if (currentTrack.Count > 0) tracks.Add(CreateTrack(currentTrack, videoId, identityId));

        return tracks;
    }

    private static FaceTrack CreateTrack(
        List<FaceAppearance> appearances,
        Guid videoId,
        Guid identityId)
    {
        var bestAppearance = appearances
            .OrderByDescending(a => a.Confidence)
            .ThenByDescending(a => a.BoundingBox.Width * a.BoundingBox.Height)
            .First();

        return new FaceTrack
        {
            Id = Guid.NewGuid(),
            VideoId = videoId,
            FaceIdentityId = identityId,
            StartTime = appearances.Min(a => a.Timestamp),
            EndTime = appearances.Max(a => a.Timestamp),
            StartFrame = appearances.Min(a => a.FrameIndex),
            EndFrame = appearances.Max(a => a.FrameIndex),
            Appearances = appearances,
            AverageConfidence = appearances.Average(a => a.Confidence),
            BestFrameIndex = bestAppearance.FrameIndex,
            QualityScore = ComputeQualityScore(appearances)
        };
    }

    private static double ComputeQualityScore(List<FaceAppearance> appearances)
    {
        if (appearances.Count == 0)
            return 0;

        // Score based on:
        // - Average confidence
        // - Average face size
        // - Number of appearances (more = better track)

        var avgConfidence = appearances.Average(a => a.Confidence);
        var avgSize = appearances.Average(a => a.BoundingBox.Width * a.BoundingBox.Height);
        var countBonus = Math.Min(appearances.Count / 10.0, 1.0);

        // Normalize size (assume 100x100 is a good face size)
        var sizeScore = Math.Min(avgSize / 10000.0, 1.0);

        return avgConfidence * 0.4 + sizeScore * 0.3 + countBonus * 0.3;
    }

    private async Task TryMatchCastMembersAsync(
        List<FaceTrack> tracks,
        List<CastMember> castMembers,
        CancellationToken ct)
    {
        // For each track, suggest potential cast member matches
        foreach (var track in tracks)
        {
            var identity = _faceTracking.GetAllIdentities()
                .FirstOrDefault(i => i.Id == track.FaceIdentityId);

            if (identity == null || !string.IsNullOrEmpty(identity.KnownName))
                continue;

            // Get name suggestions from cast
            foreach (var cast in castMembers.Take(10))
                // In a real implementation, we'd compare face embeddings
                // For now, just log that cast matching is available
                _logger.LogDebug("Cast member available for matching: {Name} as {Character}",
                    cast.Name, cast.Character);
        }

        await Task.CompletedTask;
    }
}

/// <summary>
///     Face embedding data from ImageSummarizer's face detection.
/// </summary>
public record FaceEmbeddingData
{
    public float[] Embedding { get; init; } = [];
    public double Confidence { get; init; }
    public BoundingBoxData BoundingBox { get; init; } = new();
}

public record BoundingBoxData
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}
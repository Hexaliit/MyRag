using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services.Utilities;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Stage 4: Multi-signal scene clustering.
///     Groups shots into coherent scenes using:
///     - CLIP embedding similarity (when available)
///     - Transcript semantic boundaries (topic shifts)
///     - Shot cut types (fades often indicate scene changes)
///     - Temporal windowing (reasonable scene durations)
/// </summary>
public class SceneClusteringWave : IVideoWave, ISignalAwareVideoWave
{
    // Clustering thresholds (should be in YAML)
    private const double EmbeddingSimilarityThreshold = 0.65; // Lower = more sensitive to changes
    private const double MinSceneDuration = 15.0; // Minimum scene duration in seconds
    private const double MaxSceneDuration = 300.0; // Maximum scene duration (5 minutes)
    private const double TargetSceneDuration = 90.0; // Target scene duration for fallback
    private const int MinShotsPerScene = 3;

    // Weights for multi-signal scoring
    private const double EmbeddingWeight = 0.4;
    private const double TranscriptWeight = 0.3;
    private const double CutTypeWeight = 0.2;
    private const double TemporalWeight = 0.1;
    private readonly ILogger<SceneClusteringWave> _logger;

    public SceneClusteringWave(ILogger<SceneClusteringWave> logger)
    {
        _logger = logger;
    }

    // Signal contracts
    public IReadOnlyList<string> RequiredSignals => [VideoSignals.ShotsDetected];

    public IReadOnlyList<string> OptionalSignals =>
    [
        VideoSignals.ClipEmbeddingsReady,
        VideoSignals.TranscriptionComplete,
        VideoSignals.KeyframesDeduplicated
    ];

    public IReadOnlyList<string> EmittedSignals =>
    [
        VideoSignals.ScenesDetected,
        "scene.count",
        "scene.avg_duration",
        "scene.clustering_method"
    ];

    public IReadOnlyList<string> CacheEmits => ["scene_centroids"];
    public IReadOnlyList<string> CacheUses => [];

    public string Name => "scene_clustering";
    public int Priority => 400;
    public IReadOnlyList<string> Tags => [VideoSignalTags.Scene, VideoSignalTags.Visual];

    public bool ShouldRun(VideoContext context)
    {
        if (context.Shots.Count == 0)
        {
            _logger.LogInformation("No shots - skipping scene clustering");
            return false;
        }

        return true;
    }

    public Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        context.ReportProgress("Clustering scenes", 0);

        // Emit wave started signal
        context.AddSignal(new VideoSignal
        {
            Key = $"{Name}.started",
            Value = DateTimeOffset.UtcNow,
            Source = Name,
            Tags = [VideoSignalTags.Scene, "wave", "flow"]
        });

        var hasEmbeddings = context.KeyframeEmbeddings.Count > 0;
        var hasTranscripts = context.Utterances.Count > 0;

        _logger.LogInformation(
            "Scene clustering: {Shots} shots, {Embeddings} embeddings, {Utterances} utterances",
            context.Shots.Count, context.KeyframeEmbeddings.Count, context.Utterances.Count);

        // If scenes already exist from chapters, enhance them
        if (context.Scenes.Count > 0)
        {
            context.ReportProgress("Enhancing chapter scenes", 20);
            EnhanceExistingScenes(context);
        }
        else
        {
            // Create scenes using multi-signal analysis
            context.ReportProgress("Analyzing scene boundaries", 20);
            CreateScenesMultiSignal(context, hasEmbeddings, hasTranscripts);
        }

        // Extract key terms from utterances/text tracks per scene
        context.ReportProgress("Extracting scene key terms", 70);
        ExtractSceneKeyTerms(context);

        // Compute scene centroids
        context.ReportProgress("Computing scene centroids", 85);
        ComputeSceneCentroids(context);

        // Add summary signals
        var clusteringMethod = (hasEmbeddings, hasTranscripts) switch
        {
            (true, true) => "multi_signal",
            (true, false) => "embedding_only",
            (false, true) => "transcript_only",
            _ => "temporal_windowing"
        };

        context.AddSignals([
            new VideoSignal
            {
                Key = VideoSignals.ScenesDetected,
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "scene.count",
                Value = context.Scenes.Count,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "scene.avg_duration",
                Value = context.Scenes.Count > 0
                    ? context.Scenes.Average(s => s.EndTime - s.StartTime)
                    : 0,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            },
            new VideoSignal
            {
                Key = "scene.clustering_method",
                Value = clusteringMethod,
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            }
        ]);

        sw.Stop();

        // Emit wave completed signal with timing
        context.AddSignals([
            new VideoSignal
            {
                Key = $"{Name}.completed",
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Scene, "wave", "flow"]
            },
            new VideoSignal
            {
                Key = $"{Name}.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Scene, "timing", "persist"]
            }
        ]);

        context.ReportProgress("Scene clustering complete", 100);

        _logger.LogInformation("Created {Count} scenes from {Shots} shots using {Method} in {Time}ms",
            context.Scenes.Count, context.Shots.Count, clusteringMethod, sw.ElapsedMilliseconds);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Create scenes using multiple signals: embeddings, transcripts, cut types, and temporal patterns.
    /// </summary>
    private void CreateScenesMultiSignal(VideoContext context, bool hasEmbeddings, bool hasTranscripts)
    {
        var shots = context.Shots.OrderBy(s => s.StartTime).ToList();
        if (shots.Count == 0) return;

        // Step 1: Compute boundary scores for each shot transition
        var boundaryScores = ComputeBoundaryScores(context, shots, hasEmbeddings, hasTranscripts);

        // Step 2: Find optimal scene boundaries using dynamic programming
        var boundaries = FindOptimalBoundaries(shots, boundaryScores);

        // Step 3: Create scenes from boundaries
        CreateScenesFromBoundaries(context, shots, boundaries);

        _logger.LogInformation("Multi-signal clustering: found {Boundaries} boundaries for {Scenes} scenes",
            boundaries.Count, context.Scenes.Count);
    }

    /// <summary>
    ///     Compute a boundary score for each shot transition.
    ///     Higher score = more likely to be a scene boundary.
    /// </summary>
    private List<double> ComputeBoundaryScores(
        VideoContext context,
        List<ShotSegment> shots,
        bool hasEmbeddings,
        bool hasTranscripts)
    {
        var scores = new List<double>();

        // Build embedding lookup with nearest-neighbor interpolation
        var shotEmbeddings = BuildShotEmbeddingMap(context, shots);

        // Build transcript windows for semantic analysis
        var transcriptWindows = hasTranscripts
            ? BuildTranscriptWindows(context, shots)
            : new Dictionary<int, string>();

        for (var i = 0; i < shots.Count - 1; i++)
        {
            var currentShot = shots[i];
            var nextShot = shots[i + 1];
            var score = 0.0;

            // 1. Embedding dissimilarity (if available)
            if (shotEmbeddings.TryGetValue(i, out var currentEmbed) &&
                shotEmbeddings.TryGetValue(i + 1, out var nextEmbed))
            {
                var similarity = CosineSimilarity(currentEmbed, nextEmbed);
                var dissimilarity = 1.0 - similarity;
                score += dissimilarity * EmbeddingWeight;
            }

            // 2. Transcript semantic shift
            if (transcriptWindows.TryGetValue(i, out var currentText) &&
                transcriptWindows.TryGetValue(i + 1, out var nextText))
            {
                var textSimilarity = ComputeTextSimilarity(currentText, nextText);
                var textDissimilarity = 1.0 - textSimilarity;
                score += textDissimilarity * TranscriptWeight;
            }

            // 3. Cut type signal (fades often indicate scene changes)
            if (nextShot.CutType == CutType.Fade || nextShot.CutType == CutType.Dissolve) score += CutTypeWeight;

            // 4. Temporal gap (longer gaps suggest boundaries)
            var gap = nextShot.StartTime - currentShot.EndTime;
            if (gap > 0.5) // More than 0.5 second gap
                score += Math.Min(gap / 5.0, 1.0) * TemporalWeight;

            scores.Add(score);
        }

        return scores;
    }

    /// <summary>
    ///     Build a mapping from shot index to embedding, using nearest-neighbor interpolation.
    /// </summary>
    private Dictionary<int, float[]> BuildShotEmbeddingMap(VideoContext context, List<ShotSegment> shots)
    {
        var map = new Dictionary<int, float[]>();

        // First, get all keyframes that have embeddings
        var embeddingsByTime = new List<(double Time, int ShotIndex, float[] Embedding)>();

        for (var i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            if (context.KeyframeEmbeddings.TryGetValue(shot.KeyframeIndex, out var embedding))
            {
                embeddingsByTime.Add((shot.StartTime, i, embedding));
                map[i] = embedding;
            }
        }

        if (embeddingsByTime.Count == 0) return map;

        // Propagate embeddings to nearby shots using nearest-neighbor
        for (var i = 0; i < shots.Count; i++)
        {
            if (map.ContainsKey(i)) continue;

            var shot = shots[i];
            var shotTime = (shot.StartTime + shot.EndTime) / 2;

            // Find nearest embedding
            var nearest = embeddingsByTime
                .OrderBy(e => Math.Abs(e.Time - shotTime))
                .FirstOrDefault();

            if (nearest.Embedding != null)
            {
                // Only propagate if within reasonable distance (30 seconds)
                var distance = Math.Abs(nearest.Time - shotTime);
                if (distance < 30.0) map[i] = nearest.Embedding;
            }
        }

        return map;
    }

    /// <summary>
    ///     Build transcript windows for each shot (aggregating nearby utterances).
    /// </summary>
    private Dictionary<int, string> BuildTranscriptWindows(VideoContext context, List<ShotSegment> shots)
    {
        var windows = new Dictionary<int, string>();
        var windowDuration = 10.0; // 10 second windows

        for (var i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            var windowStart = shot.StartTime - windowDuration / 2;
            var windowEnd = shot.EndTime + windowDuration / 2;

            var utterances = context.Utterances
                .Where(u => u.StartTime < windowEnd && u.EndTime > windowStart)
                .OrderBy(u => u.StartTime)
                .Select(u => u.Text)
                .ToList();

            if (utterances.Count > 0) windows[i] = string.Join(" ", utterances);
        }

        return windows;
    }

    /// <summary>
    ///     Compute text similarity using word overlap (simple Jaccard-like metric).
    /// </summary>
    private static double ComputeTextSimilarity(string text1, string text2)
    {
        var words1 = ExtractSignificantWords(text1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var words2 = ExtractSignificantWords(text2).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (words1.Count == 0 || words2.Count == 0) return 0.5; // Neutral

        var intersection = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();
        var union = words1.Union(words2, StringComparer.OrdinalIgnoreCase).Count();

        return union > 0 ? (double)intersection / union : 0;
    }

    /// <summary>
    ///     Find optimal scene boundaries using greedy approach with constraints.
    /// </summary>
    private List<int> FindOptimalBoundaries(List<ShotSegment> shots, List<double> boundaryScores)
    {
        var boundaries = new List<int>();
        var lastBoundary = 0;

        // Compute adaptive threshold based on score distribution
        var sortedScores = boundaryScores.Where(s => s > 0).OrderByDescending(s => s).ToList();
        var threshold = sortedScores.Count > 0
            ? sortedScores[Math.Min(sortedScores.Count / 4, sortedScores.Count - 1)] // Top 25%
            : 0.3;

        _logger.LogDebug("Boundary threshold: {Threshold:F3} (from {Count} scores)", threshold, sortedScores.Count);

        for (var i = 0; i < boundaryScores.Count; i++)
        {
            var shot = shots[i];
            var timeSinceLastBoundary = shot.EndTime - shots[lastBoundary].StartTime;

            // Force boundary if we've exceeded max duration
            if (timeSinceLastBoundary >= MaxSceneDuration)
            {
                boundaries.Add(i);
                lastBoundary = i + 1;
                continue;
            }

            // Check if this is a good boundary point
            if (boundaryScores[i] >= threshold && timeSinceLastBoundary >= MinSceneDuration)
            {
                // Check if we have enough shots
                var shotsSinceBoundary = i - lastBoundary + 1;
                if (shotsSinceBoundary >= MinShotsPerScene)
                {
                    boundaries.Add(i);
                    lastBoundary = i + 1;
                }
            }
        }

        // If no boundaries found, use temporal windowing
        if (boundaries.Count == 0)
        {
            _logger.LogWarning("No natural boundaries found - using temporal windowing");
            boundaries = CreateTemporalBoundaries(shots);
        }

        return boundaries;
    }

    /// <summary>
    ///     Create boundaries based on fixed time windows (fallback).
    /// </summary>
    private List<int> CreateTemporalBoundaries(List<ShotSegment> shots)
    {
        var boundaries = new List<int>();
        var windowStart = shots[0].StartTime;

        for (var i = 0; i < shots.Count - 1; i++)
        {
            var elapsed = shots[i].EndTime - windowStart;
            if (elapsed >= TargetSceneDuration)
            {
                boundaries.Add(i);
                windowStart = shots[i + 1].StartTime;
            }
        }

        return boundaries;
    }

    /// <summary>
    ///     Create scene segments from boundary indices.
    /// </summary>
    private void CreateScenesFromBoundaries(VideoContext context, List<ShotSegment> shots, List<int> boundaries)
    {
        var sceneStarts = new List<int> { 0 };
        sceneStarts.AddRange(boundaries.Select(b => b + 1));

        var sceneEnds = new List<int>(boundaries);
        sceneEnds.Add(shots.Count - 1);

        for (var i = 0; i < sceneStarts.Count; i++)
        {
            var startIdx = sceneStarts[i];
            var endIdx = sceneEnds[i];

            if (startIdx > endIdx || startIdx >= shots.Count) continue;

            var sceneShots = shots.Skip(startIdx).Take(endIdx - startIdx + 1).ToList();
            if (sceneShots.Count == 0) continue;

            var scene = new SceneSegment
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata!.Id,
                StartTime = sceneShots.First().StartTime,
                EndTime = sceneShots.Last().EndTime,
                Confidence = 0.8
            };

            scene.ShotIds.AddRange(sceneShots.Select(s => s.Id));
            scene.KeyframeIndices.AddRange(sceneShots.Select(s => s.KeyframeIndex));

            context.Scenes.Add(scene);
        }
    }

    /// <summary>
    ///     Enhance existing scenes (from chapters) with shot mappings.
    /// </summary>
    private void EnhanceExistingScenes(VideoContext context)
    {
        foreach (var scene in context.Scenes)
        {
            var sceneShots = context.Shots
                .Where(s => s.StartTime >= scene.StartTime && s.EndTime <= scene.EndTime)
                .ToList();

            if (sceneShots.Count == 0) continue;

            var existingIds = scene.ShotIds.ToHashSet();
            foreach (var shot in sceneShots)
                if (!existingIds.Contains(shot.Id))
                {
                    scene.ShotIds.Add(shot.Id);
                    scene.KeyframeIndices.Add(shot.KeyframeIndex);
                }
        }
    }

    /// <summary>
    ///     Extract key terms from utterances and text tracks in each scene.
    /// </summary>
    private void ExtractSceneKeyTerms(VideoContext context)
    {
        foreach (var scene in context.Scenes)
        {
            var terms = new List<string>();

            var sceneUtterances = context.Utterances
                .Where(u => u.StartTime < scene.EndTime && u.EndTime > scene.StartTime)
                .ToList();

            foreach (var utterance in sceneUtterances)
            {
                var words = ExtractSignificantWords(utterance.Text);
                terms.AddRange(words);
            }

            var sceneTextTracks = context.TextTracks
                .Where(t => t.StartTime < scene.EndTime && t.EndTime > scene.StartTime)
                .ToList();

            foreach (var track in sceneTextTracks)
            {
                var words = ExtractSignificantWords(track.Text);
                terms.AddRange(words);
            }

            var topTerms = terms
                .GroupBy(t => t.ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => g.First())
                .ToList();

            scene.KeyTerms.AddRange(topTerms);

            var speakerIds = sceneUtterances
                .Select(u => context.GetCached<string>($"utterance_speaker.{u.Id}"))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            scene.SpeakerIds.AddRange(speakerIds!);
        }
    }

    /// <summary>
    ///     Compute centroid embeddings for each scene.
    /// </summary>
    private void ComputeSceneCentroids(VideoContext context)
    {
        foreach (var scene in context.Scenes)
        {
            var embeddings = new List<float[]>();

            foreach (var frameIndex in scene.KeyframeIndices)
                if (context.KeyframeEmbeddings.TryGetValue(frameIndex, out var embedding))
                    embeddings.Add(embedding);

            if (embeddings.Count == 0) continue;

            var dimension = embeddings[0].Length;
            var centroid = new float[dimension];

            for (var d = 0; d < dimension; d++) centroid[d] = embeddings.Average(e => e[d]);

            var norm = (float)Math.Sqrt(centroid.Sum(x => x * x));
            if (norm > 1e-6)
                for (var d = 0; d < dimension; d++)
                    centroid[d] /= norm;

            context.SetCached($"scene_centroid.{scene.Id}", centroid);
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 1e-10 ? dot / denom : 0;
    }

    private static List<string> ExtractSignificantWords(string text)
    {
        // Use shared stopword list from StopwordLists utility
        return text
            .Split([' ', '\t', '\n', '\r', '.', ',', '!', '?', ':', ';', '"', '\'', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopwordLists.ShouldFilter(w) && !int.TryParse(w, out _))
            .Distinct()
            .ToList();
    }
}
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Stage 4: Scene clustering using keyframe embeddings.
/// Groups adjacent shots with similar visual content into coherent scenes.
/// Uses cosine similarity of CLIP embeddings from ImageSummarizer.
/// </summary>
public class SceneClusteringWave : IVideoWave
{
    private readonly ILogger<SceneClusteringWave> _logger;

    // Clustering thresholds
    private const double SimilarityThreshold = 0.7; // Shots must be this similar to merge
    private const double MinSceneDuration = 5.0; // Minimum scene duration in seconds
    private const int MinShotsPerScene = 2; // Minimum shots to form a scene

    public string Name => "scene_clustering";
    public int Priority => 400; // After transcription
    public IReadOnlyList<string> Tags => [VideoSignalTags.Scene, VideoSignalTags.Visual];

    public SceneClusteringWave(ILogger<SceneClusteringWave> logger)
    {
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context)
    {
        // Only run if we have keyframe embeddings and shots
        if (context.Shots.Count == 0)
        {
            _logger.LogInformation("No shots - skipping scene clustering");
            return false;
        }

        // Skip if we already have scenes from chapters
        if (context.Scenes.Count > 0)
        {
            _logger.LogInformation("Scenes already exist from chapters - enhancing with embeddings");
        }

        return true;
    }

    public Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Clustering scenes", 0);

        // Check if we have embeddings
        var hasEmbeddings = context.KeyframeEmbeddings.Count > 0;

        if (!hasEmbeddings)
        {
            _logger.LogWarning("No keyframe embeddings - using temporal clustering only");
        }

        // If scenes already exist from chapters, enhance them with embeddings
        if (context.Scenes.Count > 0)
        {
            context.ReportProgress("Enhancing chapter scenes", 20);
            EnhanceExistingScenes(context);
        }
        else
        {
            // Create scenes from scratch using embedding similarity
            context.ReportProgress("Creating scenes from shots", 20);

            if (hasEmbeddings)
            {
                CreateScenesFromEmbeddings(context);
            }
            else
            {
                CreateScenesFromTemporal(context);
            }
        }

        // Extract key terms from utterances/text tracks per scene
        context.ReportProgress("Extracting scene key terms", 70);
        ExtractSceneKeyTerms(context);

        // Compute scene centroids
        context.ReportProgress("Computing scene centroids", 85);
        ComputeSceneCentroids(context);

        // Add summary signals
        context.AddSignals([
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
                Value = hasEmbeddings ? "embedding_similarity" : "temporal",
                Source = Name,
                Tags = [VideoSignalTags.Scene]
            }
        ]);

        context.ReportProgress("Scene clustering complete", 100);

        _logger.LogInformation("Created {Count} scenes from {Shots} shots",
            context.Scenes.Count, context.Shots.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Create scenes by clustering adjacent shots with similar embeddings.
    /// Uses a sliding window approach to find natural boundaries.
    /// </summary>
    private void CreateScenesFromEmbeddings(VideoContext context)
    {
        var shots = context.Shots.OrderBy(s => s.StartTime).ToList();
        var currentSceneShots = new List<ShotSegment>();
        var sceneStart = 0.0;

        for (int i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            currentSceneShots.Add(shot);

            // Check if this is a scene boundary
            var isBoundary = false;

            if (i < shots.Count - 1)
            {
                var nextShot = shots[i + 1];

                // Get embeddings for current and next shots
                var currentEmbed = GetShotEmbedding(context, shot);
                var nextEmbed = GetShotEmbedding(context, nextShot);

                if (currentEmbed != null && nextEmbed != null)
                {
                    var similarity = CosineSimilarity(currentEmbed, nextEmbed);

                    // Low similarity = scene boundary
                    isBoundary = similarity < SimilarityThreshold;

                    _logger.LogDebug("Shot {Current} -> {Next}: similarity={Sim:F3}, boundary={IsBoundary}",
                        i, i + 1, similarity, isBoundary);
                }
                else
                {
                    // No embeddings - use temporal gap heuristic
                    var gap = nextShot.StartTime - shot.EndTime;
                    isBoundary = gap > 1.0; // 1 second gap suggests boundary
                }
            }
            else
            {
                // Last shot - always end scene
                isBoundary = true;
            }

            if (isBoundary && currentSceneShots.Count >= MinShotsPerScene)
            {
                var sceneEnd = shot.EndTime;
                var duration = sceneEnd - sceneStart;

                if (duration >= MinSceneDuration)
                {
                    var scene = new SceneSegment
                    {
                        Id = Guid.NewGuid(),
                        VideoId = context.Metadata!.Id,
                        StartTime = sceneStart,
                        EndTime = sceneEnd,
                        Confidence = 0.8
                    };

                    scene.ShotIds.AddRange(currentSceneShots.Select(s => s.Id));
                    scene.KeyframeIndices.AddRange(currentSceneShots.Select(s => s.KeyframeIndex));

                    context.Scenes.Add(scene);

                    currentSceneShots.Clear();
                    sceneStart = shots[i + 1 < shots.Count ? i + 1 : i].StartTime;
                }
            }
        }

        // Handle remaining shots
        if (currentSceneShots.Count > 0)
        {
            var lastShot = currentSceneShots.Last();
            var scene = new SceneSegment
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata!.Id,
                StartTime = sceneStart,
                EndTime = lastShot.EndTime,
                Confidence = 0.7
            };

            scene.ShotIds.AddRange(currentSceneShots.Select(s => s.Id));
            scene.KeyframeIndices.AddRange(currentSceneShots.Select(s => s.KeyframeIndex));

            context.Scenes.Add(scene);
        }
    }

    /// <summary>
    /// Create scenes using only temporal information (fallback when no embeddings).
    /// </summary>
    private void CreateScenesFromTemporal(VideoContext context)
    {
        var shots = context.Shots.OrderBy(s => s.StartTime).ToList();
        var sceneDuration = 30.0; // Default scene duration when no embeddings
        var currentSceneShots = new List<ShotSegment>();
        var sceneStart = 0.0;

        foreach (var shot in shots)
        {
            currentSceneShots.Add(shot);

            if (shot.EndTime - sceneStart >= sceneDuration)
            {
                var scene = new SceneSegment
                {
                    Id = Guid.NewGuid(),
                    VideoId = context.Metadata!.Id,
                    StartTime = sceneStart,
                    EndTime = shot.EndTime,
                    Confidence = 0.5 // Lower confidence for temporal-only clustering
                };

                scene.ShotIds.AddRange(currentSceneShots.Select(s => s.Id));
                scene.KeyframeIndices.AddRange(currentSceneShots.Select(s => s.KeyframeIndex));

                context.Scenes.Add(scene);

                currentSceneShots.Clear();
                sceneStart = shot.EndTime;
            }
        }

        // Handle remaining shots
        if (currentSceneShots.Count > 0)
        {
            var lastShot = currentSceneShots.Last();
            var scene = new SceneSegment
            {
                Id = Guid.NewGuid(),
                VideoId = context.Metadata!.Id,
                StartTime = sceneStart,
                EndTime = lastShot.EndTime,
                Confidence = 0.5
            };

            scene.ShotIds.AddRange(currentSceneShots.Select(s => s.Id));
            scene.KeyframeIndices.AddRange(currentSceneShots.Select(s => s.KeyframeIndex));

            context.Scenes.Add(scene);
        }
    }

    /// <summary>
    /// Enhance existing scenes (from chapters) with embedding centroids.
    /// </summary>
    private void EnhanceExistingScenes(VideoContext context)
    {
        foreach (var scene in context.Scenes)
        {
            // Find shots that belong to this scene by time overlap
            var sceneShots = context.Shots
                .Where(s => s.StartTime >= scene.StartTime && s.EndTime <= scene.EndTime)
                .ToList();

            if (sceneShots.Count == 0) continue;

            // Add shot IDs if not already present
            var existingIds = scene.ShotIds.ToHashSet();
            foreach (var shot in sceneShots)
            {
                if (!existingIds.Contains(shot.Id))
                {
                    scene.ShotIds.Add(shot.Id);
                    scene.KeyframeIndices.Add(shot.KeyframeIndex);
                }
            }
        }
    }

    /// <summary>
    /// Extract key terms from utterances and text tracks in each scene.
    /// </summary>
    private void ExtractSceneKeyTerms(VideoContext context)
    {
        foreach (var scene in context.Scenes)
        {
            var terms = new List<string>();

            // Get utterances in this scene
            var sceneUtterances = context.Utterances
                .Where(u => u.StartTime < scene.EndTime && u.EndTime > scene.StartTime)
                .ToList();

            // Extract significant words from utterances
            foreach (var utterance in sceneUtterances)
            {
                var words = ExtractSignificantWords(utterance.Text);
                terms.AddRange(words);
            }

            // Get text tracks in this scene
            var sceneTextTracks = context.TextTracks
                .Where(t => t.StartTime < scene.EndTime && t.EndTime > scene.StartTime)
                .ToList();

            // Add text track content
            foreach (var track in sceneTextTracks)
            {
                var words = ExtractSignificantWords(track.Text);
                terms.AddRange(words);
            }

            // Get top terms by frequency
            var topTerms = terms
                .GroupBy(t => t.ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => g.First())
                .ToList();

            scene.KeyTerms.AddRange(topTerms);

            // Add speaker IDs from utterances
            var speakerIds = sceneUtterances
                .Select(u => context.GetCached<string>($"utterance_speaker.{u.Id}"))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            scene.SpeakerIds.AddRange(speakerIds!);
        }
    }

    /// <summary>
    /// Compute centroid embeddings for each scene.
    /// </summary>
    private void ComputeSceneCentroids(VideoContext context)
    {
        foreach (var scene in context.Scenes)
        {
            var embeddings = new List<float[]>();

            // Collect embeddings from scene's keyframes
            foreach (var frameIndex in scene.KeyframeIndices)
            {
                if (context.KeyframeEmbeddings.TryGetValue(frameIndex, out var embedding))
                {
                    embeddings.Add(embedding);
                }
            }

            if (embeddings.Count == 0) continue;

            // Compute centroid (average of embeddings)
            var dimension = embeddings[0].Length;
            var centroid = new float[dimension];

            for (int d = 0; d < dimension; d++)
            {
                centroid[d] = embeddings.Average(e => e[d]);
            }

            // Normalize centroid
            var norm = (float)Math.Sqrt(centroid.Sum(x => x * x));
            if (norm > 1e-6)
            {
                for (int d = 0; d < dimension; d++)
                {
                    centroid[d] /= norm;
                }
            }

            // Store centroid - SceneSegment is a record, use cache
            context.SetCached($"scene_centroid.{scene.Id}", centroid);
        }
    }

    private float[]? GetShotEmbedding(VideoContext context, ShotSegment shot)
    {
        // Try keyframe index
        if (context.KeyframeEmbeddings.TryGetValue(shot.KeyframeIndex, out var embedding))
        {
            return embedding;
        }

        // Try cached shot embedding
        return context.GetCached<float[]>($"shot_embedding.{shot.Id}");
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
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
        // Simple extraction - in production, use NER or TF-IDF
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "must", "shall", "can", "need", "dare",
            "to", "of", "in", "for", "on", "with", "at", "by", "from", "as",
            "into", "through", "during", "before", "after", "above", "below",
            "between", "under", "again", "further", "then", "once", "here",
            "there", "when", "where", "why", "how", "all", "each", "few",
            "more", "most", "other", "some", "such", "no", "nor", "not",
            "only", "own", "same", "so", "than", "too", "very", "just",
            "and", "but", "if", "or", "because", "until", "while", "this",
            "that", "these", "those", "it", "its", "i", "me", "my", "we",
            "our", "you", "your", "he", "him", "his", "she", "her", "they",
            "them", "their", "what", "which", "who", "whom"
        };

        return text
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ':', ';', '"', '\'', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .ToList();
    }
}

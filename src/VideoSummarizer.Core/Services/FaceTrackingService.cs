using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services;

/// <summary>
///     Service for tracking faces across videos and managing persistent face identities.
///     Provides functionality similar to Immich's face recognition system.
///     Privacy-preserving: uses embeddings only, no face images stored.
/// </summary>
public class FaceTrackingService
{
    private readonly object _lock = new();
    private readonly ILogger<FaceTrackingService> _logger;

    // In-memory face database (should be persisted to DB in production)
    private FaceDatabase _database = new()
    {
        Id = Guid.NewGuid(),
        Name = "Default"
    };

    public FaceTrackingService(ILogger<FaceTrackingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Load or create a face database.
    /// </summary>
    public FaceDatabase GetOrCreateDatabase(string name = "Default")
    {
        lock (_lock)
        {
            return _database;
        }
    }

    /// <summary>
    ///     Match a face embedding against known identities.
    ///     Creates a new identity if no match found and options allow it.
    /// </summary>
    public FaceMatchResult MatchFace(
        float[] embedding,
        Guid videoId,
        FaceMatchOptions? options = null)
    {
        options ??= new FaceMatchOptions();

        lock (_lock)
        {
            // Find best matching identity
            var matches = _database.Identities
                .Select(identity =>
                {
                    var similarity = CosineSimilarity(embedding, identity.CentroidEmbedding);
                    return (Identity: identity, Similarity: similarity);
                })
                .Where(x => x.Similarity >= options.SimilarityThreshold)
                .OrderByDescending(x => x.Similarity)
                .Take(options.MaxMatches)
                .ToList();

            if (matches.Count > 0)
            {
                var bestMatch = matches[0];

                // Update centroid if enabled
                if (options.UpdateCentroidOnMatch) UpdateCentroid(bestMatch.Identity, embedding);

                // Track video appearance
                if (!bestMatch.Identity.VideoIds.Contains(videoId)) bestMatch.Identity.VideoIds.Add(videoId);

                _logger.LogDebug("Matched face to identity {Id} with similarity {Similarity:F3}",
                    bestMatch.Identity.Id, bestMatch.Similarity);

                return new FaceMatchResult
                {
                    MatchedIdentity = bestMatch.Identity,
                    Similarity = bestMatch.Similarity,
                    IsNewIdentity = false,
                    AlternativeMatches = matches.Skip(1)
                        .Select(m => (m.Identity.Id, m.Similarity))
                        .ToList()
                };
            }

            // No match found - create new identity if allowed
            if (options.CreateNewIfNoMatch)
            {
                var newIdentity = new FaceIdentity
                {
                    Id = Guid.NewGuid(),
                    CentroidEmbedding = (float[])embedding.Clone(),
                    SampleCount = 1,
                    Confidence = 1.0,
                    FirstSeen = DateTimeOffset.UtcNow,
                    LastSeen = DateTimeOffset.UtcNow,
                    Source = IdentitySource.AutoCluster,
                    VideoIds = [videoId]
                };

                _database.Identities.Add(newIdentity);
                UpdateStats();

                _logger.LogDebug("Created new face identity {Id}", newIdentity.Id);

                return new FaceMatchResult
                {
                    MatchedIdentity = newIdentity,
                    Similarity = 1.0,
                    IsNewIdentity = true,
                    AlternativeMatches = []
                };
            }

            return new FaceMatchResult
            {
                MatchedIdentity = null,
                Similarity = 0,
                IsNewIdentity = false,
                AlternativeMatches = []
            };
        }
    }

    /// <summary>
    ///     Get all face identities from the database.
    /// </summary>
    public List<FaceIdentity> GetAllIdentities()
    {
        lock (_lock)
        {
            return _database.Identities.ToList();
        }
    }

    /// <summary>
    ///     Get face identities for a specific video.
    /// </summary>
    public List<FaceIdentity> GetIdentitiesForVideo(Guid videoId)
    {
        lock (_lock)
        {
            return _database.Identities
                .Where(i => i.VideoIds.Contains(videoId))
                .ToList();
        }
    }

    /// <summary>
    ///     Assign a name to a face identity.
    /// </summary>
    public FaceIdentity? AssignName(Guid identityId, string name, IdentitySource source = IdentitySource.UserInput)
    {
        lock (_lock)
        {
            var identity = _database.Identities.FirstOrDefault(i => i.Id == identityId);
            if (identity == null)
            {
                _logger.LogWarning("Face identity not found: {Id}", identityId);
                return null;
            }

            identity.KnownName = name;
            identity.Source = source;

            _logger.LogInformation("Assigned name '{Name}' to face identity {Id}", name, identityId);
            UpdateStats();

            return identity;
        }
    }

    /// <summary>
    ///     Set a label for a face identity.
    /// </summary>
    public FaceIdentity? SetLabel(Guid identityId, string label)
    {
        lock (_lock)
        {
            var identity = _database.Identities.FirstOrDefault(i => i.Id == identityId);
            if (identity == null) return null;

            identity.Label = label;
            return identity;
        }
    }

    /// <summary>
    ///     Merge multiple face identities into one.
    /// </summary>
    public FaceIdentity? MergeIdentities(MergeIdentitiesRequest request)
    {
        lock (_lock)
        {
            var primary = _database.Identities.FirstOrDefault(i => i.Id == request.PrimaryIdentityId);
            if (primary == null)
            {
                _logger.LogWarning("Primary identity not found: {Id}", request.PrimaryIdentityId);
                return null;
            }

            var secondaries = _database.Identities
                .Where(i => request.SecondaryIdentityIds.Contains(i.Id))
                .ToList();

            if (secondaries.Count == 0)
            {
                _logger.LogWarning("No secondary identities found for merge");
                return primary;
            }

            // Merge embeddings (weighted average by sample count)
            var allEmbeddings = new List<(float[] Embedding, int Weight)>
            {
                (primary.CentroidEmbedding, primary.SampleCount)
            };

            foreach (var secondary in secondaries)
            {
                allEmbeddings.Add((secondary.CentroidEmbedding, secondary.SampleCount));

                // Merge video IDs
                foreach (var videoId in secondary.VideoIds)
                    if (!primary.VideoIds.Contains(videoId))
                        primary.VideoIds.Add(videoId);

                // Remove secondary from database
                _database.Identities.Remove(secondary);
            }

            // Update centroid with weighted average
            var totalWeight = allEmbeddings.Sum(e => e.Weight);
            var newCentroid = new float[primary.CentroidEmbedding.Length];
            for (var i = 0; i < newCentroid.Length; i++)
                newCentroid[i] = allEmbeddings.Sum(e => e.Embedding[i] * e.Weight) / totalWeight;

            // Normalize centroid
            var norm = (float)Math.Sqrt(newCentroid.Sum(x => x * x));
            if (norm > 0)
                for (var i = 0; i < newCentroid.Length; i++)
                    newCentroid[i] /= norm;

            // Update primary identity (create new record since it's immutable)
            var updated = primary with
            {
                CentroidEmbedding = newCentroid,
                SampleCount = totalWeight,
                KnownName = request.NewDisplayName ?? primary.KnownName,
                LastSeen = DateTimeOffset.UtcNow
            };

            // Replace in list
            _database.Identities.Remove(primary);
            _database.Identities.Add(updated);

            UpdateStats();

            _logger.LogInformation("Merged {Count} identities into {Id}",
                secondaries.Count + 1, updated.Id);

            return updated;
        }
    }

    /// <summary>
    ///     Split a face identity into multiple identities.
    /// </summary>
    public List<FaceIdentity> SplitIdentity(SplitIdentityRequest request)
    {
        // This would require face track data to know which embeddings to split
        // For now, just log that this needs the face tracks
        _logger.LogWarning("SplitIdentity requires face track data - not yet implemented");
        return [];
    }

    /// <summary>
    ///     Match face to cast member based on embedding similarity.
    /// </summary>
    public FaceIdentity? MatchToCastMember(
        Guid identityId,
        CastMember castMember,
        double minSimilarity = 0.7)
    {
        if (castMember.FaceEmbedding == null)
        {
            _logger.LogWarning("Cast member {Name} has no face embedding", castMember.Name);
            return null;
        }

        lock (_lock)
        {
            var identity = _database.Identities.FirstOrDefault(i => i.Id == identityId);
            if (identity == null) return null;

            var similarity = CosineSimilarity(identity.CentroidEmbedding, castMember.FaceEmbedding);
            if (similarity >= minSimilarity)
            {
                identity.KnownName = castMember.Name;
                identity.Source = IdentitySource.CastMatch;

                _logger.LogInformation("Matched identity {Id} to cast member {Name} with similarity {Similarity:F3}",
                    identityId, castMember.Name, similarity);

                return identity;
            }

            return null;
        }
    }

    /// <summary>
    ///     Get face database statistics.
    /// </summary>
    public FaceDatabaseStats GetStats()
    {
        lock (_lock)
        {
            return _database.Stats;
        }
    }

    /// <summary>
    ///     Get suggestions for naming a face identity.
    /// </summary>
    public List<IdentitySuggestion> GetNameSuggestions(
        Guid identityId,
        ExternalMediaMetadata? mediaMetadata)
    {
        var suggestions = new List<IdentitySuggestion>();

        if (mediaMetadata == null) return suggestions;

        lock (_lock)
        {
            var identity = _database.Identities.FirstOrDefault(i => i.Id == identityId);
            if (identity == null) return suggestions;

            // Suggest cast members
            foreach (var cast in mediaMetadata.Cast.Take(10))
                suggestions.Add(new IdentitySuggestion
                {
                    FaceIdentityId = identityId,
                    SuggestedName = cast.Name,
                    Source = SuggestionSource.CastMetadata,
                    Confidence = 0.5 - (cast.Order ?? 0) * 0.05, // Higher confidence for main cast
                    Evidence = $"Cast member ({cast.Character ?? "unknown role"})"
                });

            // Suggest from directors/writers if relevant
            foreach (var director in mediaMetadata.Directors.Take(3))
                suggestions.Add(new IdentitySuggestion
                {
                    FaceIdentityId = identityId,
                    SuggestedName = director,
                    Source = SuggestionSource.CastMetadata,
                    Confidence = 0.3,
                    Evidence = "Director"
                });
        }

        return suggestions.OrderByDescending(s => s.Confidence).ToList();
    }

    private void UpdateCentroid(FaceIdentity identity, float[] newEmbedding)
    {
        // Update centroid using running average
        var count = identity.SampleCount + 1;
        var centroid = identity.CentroidEmbedding;

        for (var i = 0; i < centroid.Length && i < newEmbedding.Length; i++)
            centroid[i] = (centroid[i] * identity.SampleCount + newEmbedding[i]) / count;

        // Normalize
        var norm = (float)Math.Sqrt(centroid.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < centroid.Length; i++)
                centroid[i] /= norm;

        // Can't update immutable record, need to replace
        // In production, this would update the database
    }

    private void UpdateStats()
    {
        _database = _database with
        {
            Stats = new FaceDatabaseStats
            {
                TotalIdentities = _database.Identities.Count,
                NamedIdentities = _database.Identities.Count(i => !string.IsNullOrEmpty(i.KnownName)),
                TotalAppearances = _database.Identities.Sum(i => i.SampleCount),
                VideosProcessed = _database.Identities.SelectMany(i => i.VideoIds).Distinct().Count()
            },
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;

        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

/// <summary>
///     Result of matching a face against the database.
/// </summary>
public record FaceMatchResult
{
    public FaceIdentity? MatchedIdentity { get; init; }
    public double Similarity { get; init; }
    public bool IsNewIdentity { get; init; }
    public List<(Guid IdentityId, double Similarity)> AlternativeMatches { get; init; } = [];
}
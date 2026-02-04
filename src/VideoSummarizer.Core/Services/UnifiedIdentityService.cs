using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services;

/// <summary>
///     Service for managing unified identities across modalities.
///     Links faces (video/images), speakers (audio), and named entities (documents).
///     Enables cross-modal identity resolution: "John Smith mentioned in document X is the same person
///     seen in video Y and heard speaking in audio Z."
/// </summary>
public class UnifiedIdentityService
{
    private readonly List<IdentityAppearance> _appearances = [];
    private readonly FaceTrackingService _faceTracking;

    // In-memory storage (should be persisted to DB in production)
    private readonly List<UnifiedIdentity> _identities = [];
    private readonly Dictionary<Guid, List<IdentityLink>> _links = [];
    private readonly object _lock = new();
    private readonly ILogger<UnifiedIdentityService> _logger;

    public UnifiedIdentityService(
        FaceTrackingService faceTracking,
        ILogger<UnifiedIdentityService> logger)
    {
        _faceTracking = faceTracking;
        _logger = logger;
    }

    /// <summary>
    ///     Create a new unified identity from a name.
    /// </summary>
    public UnifiedIdentity CreateIdentity(string name, NameSource source = NameSource.UserInput)
    {
        lock (_lock)
        {
            var identity = new UnifiedIdentity
            {
                Id = Guid.NewGuid(),
                DisplayName = name,
                NameSource = source,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _identities.Add(identity);
            _logger.LogInformation("Created unified identity: {Name} (source: {Source})", name, source);

            return identity;
        }
    }

    /// <summary>
    ///     Get or create a unified identity for a named entity from a document.
    /// </summary>
    public UnifiedIdentity GetOrCreateFromDocumentEntity(
        Guid documentId,
        string entityText,
        string entityType,
        int startOffset,
        int endOffset,
        string? context = null,
        double confidence = 1.0)
    {
        lock (_lock)
        {
            // Look for existing identity with same name
            var existing = _identities.FirstOrDefault(i =>
                string.Equals(i.DisplayName, entityText, StringComparison.OrdinalIgnoreCase) ||
                i.Aliases.Any(a => string.Equals(a, entityText, StringComparison.OrdinalIgnoreCase)));

            UnifiedIdentity identity;
            if (existing != null)
            {
                identity = existing;
                _logger.LogDebug("Found existing identity for entity: {Entity}", entityText);
            }
            else
            {
                identity = new UnifiedIdentity
                {
                    Id = Guid.NewGuid(),
                    DisplayName = entityText,
                    NameSource = NameSource.DocumentNER,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _identities.Add(identity);
                _logger.LogInformation("Created identity from document entity: {Entity}", entityText);
            }

            // Add document mention
            var mention = new NamedEntityMention
            {
                DocumentId = documentId,
                EntityText = entityText,
                EntityType = entityType,
                StartOffset = startOffset,
                EndOffset = endOffset,
                Confidence = confidence,
                Context = context
            };

            if (!identity.DocumentMentions.Any(m =>
                    m.DocumentId == documentId &&
                    m.StartOffset == startOffset &&
                    m.EndOffset == endOffset))
                identity.DocumentMentions.Add(mention);

            // Track appearance
            _appearances.Add(new IdentityAppearance
            {
                Id = Guid.NewGuid(),
                UnifiedIdentityId = identity.Id,
                Type = AppearanceType.NameInDocument,
                SourceId = documentId,
                StartOffset = startOffset,
                EndOffset = endOffset,
                MentionText = entityText,
                Confidence = confidence
            });

            return identity;
        }
    }

    /// <summary>
    ///     Link a face identity to a unified identity.
    /// </summary>
    public void LinkFaceIdentity(Guid unifiedIdentityId, Guid faceIdentityId, double confidence = 1.0)
    {
        lock (_lock)
        {
            var identity = _identities.FirstOrDefault(i => i.Id == unifiedIdentityId);
            if (identity == null)
            {
                _logger.LogWarning("Unified identity not found: {Id}", unifiedIdentityId);
                return;
            }

            if (!identity.FaceIdentityIds.Contains(faceIdentityId))
            {
                identity.FaceIdentityIds.Add(faceIdentityId);
                identity.UpdatedAt = DateTimeOffset.UtcNow;

                // Also update the face identity's known name
                _faceTracking.AssignName(faceIdentityId, identity.DisplayName!, IdentitySource.CastMatch);

                _logger.LogInformation("Linked face identity {FaceId} to unified identity {Name}",
                    faceIdentityId, identity.DisplayName);

                // Track the link
                AddLink(unifiedIdentityId, new IdentityLink
                {
                    LinkType = LinkType.FaceToIdentity,
                    TargetId = faceIdentityId,
                    Confidence = confidence,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }
    }

    /// <summary>
    ///     Link a speaker ID to a unified identity.
    /// </summary>
    public void LinkSpeaker(Guid unifiedIdentityId, string speakerId, double confidence = 1.0)
    {
        lock (_lock)
        {
            var identity = _identities.FirstOrDefault(i => i.Id == unifiedIdentityId);
            if (identity == null)
            {
                _logger.LogWarning("Unified identity not found: {Id}", unifiedIdentityId);
                return;
            }

            if (!identity.SpeakerIds.Contains(speakerId))
            {
                identity.SpeakerIds.Add(speakerId);
                identity.UpdatedAt = DateTimeOffset.UtcNow;

                _logger.LogInformation("Linked speaker {SpeakerId} to unified identity {Name}",
                    speakerId, identity.DisplayName);

                AddLink(unifiedIdentityId, new IdentityLink
                {
                    LinkType = LinkType.SpeakerToIdentity,
                    TargetId = speakerId,
                    Confidence = confidence,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }
    }

    /// <summary>
    ///     Find identity by name (case-insensitive, including aliases).
    /// </summary>
    public UnifiedIdentity? FindByName(string name)
    {
        lock (_lock)
        {
            return _identities.FirstOrDefault(i =>
                string.Equals(i.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                i.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    ///     Find identity by face identity ID.
    /// </summary>
    public UnifiedIdentity? FindByFaceIdentity(Guid faceIdentityId)
    {
        lock (_lock)
        {
            return _identities.FirstOrDefault(i => i.FaceIdentityIds.Contains(faceIdentityId));
        }
    }

    /// <summary>
    ///     Find identity by speaker ID.
    /// </summary>
    public UnifiedIdentity? FindBySpeaker(string speakerId)
    {
        lock (_lock)
        {
            return _identities.FirstOrDefault(i => i.SpeakerIds.Contains(speakerId));
        }
    }

    /// <summary>
    ///     Get all unified identities.
    /// </summary>
    public List<UnifiedIdentity> GetAllIdentities()
    {
        lock (_lock)
        {
            return _identities.ToList();
        }
    }

    /// <summary>
    ///     Get identity by ID.
    /// </summary>
    public UnifiedIdentity? GetById(Guid id)
    {
        lock (_lock)
        {
            return _identities.FirstOrDefault(i => i.Id == id);
        }
    }

    /// <summary>
    ///     Search identities by partial name match.
    /// </summary>
    public List<UnifiedIdentity> SearchByName(string query, int maxResults = 10)
    {
        lock (_lock)
        {
            var lowerQuery = query.ToLowerInvariant();
            return _identities
                .Where(i =>
                    i.DisplayName?.ToLowerInvariant().Contains(lowerQuery) == true ||
                    i.Aliases.Any(a => a.ToLowerInvariant().Contains(lowerQuery)))
                .Take(maxResults)
                .ToList();
        }
    }

    /// <summary>
    ///     Add an alias to an identity.
    /// </summary>
    public void AddAlias(Guid identityId, string alias)
    {
        lock (_lock)
        {
            var identity = _identities.FirstOrDefault(i => i.Id == identityId);
            if (identity == null) return;

            if (!identity.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                identity.Aliases.Add(alias);
                identity.UpdatedAt = DateTimeOffset.UtcNow;
                _logger.LogDebug("Added alias '{Alias}' to identity {Name}", alias, identity.DisplayName);
            }
        }
    }

    /// <summary>
    ///     Merge two unified identities.
    /// </summary>
    public UnifiedIdentity? MergeIdentities(Guid primaryId, Guid secondaryId)
    {
        lock (_lock)
        {
            var primary = _identities.FirstOrDefault(i => i.Id == primaryId);
            var secondary = _identities.FirstOrDefault(i => i.Id == secondaryId);

            if (primary == null || secondary == null)
            {
                _logger.LogWarning("Cannot merge: identity not found");
                return null;
            }

            // Merge aliases
            foreach (var alias in secondary.Aliases)
                if (!primary.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    primary.Aliases.Add(alias);

            // Add secondary display name as alias if different
            if (!string.IsNullOrEmpty(secondary.DisplayName) &&
                !string.Equals(primary.DisplayName, secondary.DisplayName, StringComparison.OrdinalIgnoreCase))
                primary.Aliases.Add(secondary.DisplayName);

            // Merge face identities
            foreach (var faceId in secondary.FaceIdentityIds)
                if (!primary.FaceIdentityIds.Contains(faceId))
                    primary.FaceIdentityIds.Add(faceId);

            // Merge speaker IDs
            foreach (var speakerId in secondary.SpeakerIds)
                if (!primary.SpeakerIds.Contains(speakerId))
                    primary.SpeakerIds.Add(speakerId);

            // Merge document mentions
            foreach (var mention in secondary.DocumentMentions)
                if (!primary.DocumentMentions.Any(m =>
                        m.DocumentId == mention.DocumentId &&
                        m.StartOffset == mention.StartOffset))
                    primary.DocumentMentions.Add(mention);

            // Update appearances to point to primary
            foreach (var appearance in _appearances.Where(a => a.UnifiedIdentityId == secondaryId))
            {
                // Note: Records are immutable, so we'd need to replace
                // In production, this would be a database update
            }

            // Remove secondary
            _identities.Remove(secondary);
            primary.UpdatedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Merged identity {Secondary} into {Primary}",
                secondary.DisplayName, primary.DisplayName);

            return primary;
        }
    }

    /// <summary>
    ///     Get all appearances for an identity.
    /// </summary>
    public List<IdentityAppearance> GetAppearances(Guid identityId)
    {
        lock (_lock)
        {
            return _appearances.Where(a => a.UnifiedIdentityId == identityId).ToList();
        }
    }

    /// <summary>
    ///     Suggest potential identity matches based on name similarity.
    /// </summary>
    public List<IdentityMatchSuggestion> SuggestMatches(string name, double minConfidence = 0.5)
    {
        lock (_lock)
        {
            var suggestions = new List<IdentityMatchSuggestion>();
            var normalizedName = NormalizeName(name);

            foreach (var identity in _identities)
            {
                var displaySimilarity =
                    ComputeNameSimilarity(normalizedName, NormalizeName(identity.DisplayName ?? ""));
                var aliasSimilarity = identity.Aliases.Count > 0
                    ? identity.Aliases.Max(a => ComputeNameSimilarity(normalizedName, NormalizeName(a)))
                    : 0.0;
                var bestSimilarity = Math.Max(displaySimilarity, aliasSimilarity);

                if (bestSimilarity >= minConfidence)
                    suggestions.Add(new IdentityMatchSuggestion
                    {
                        Identity = identity,
                        Confidence = bestSimilarity,
                        MatchedOn = displaySimilarity >= aliasSimilarity ? identity.DisplayName : "alias"
                    });
            }

            return suggestions.OrderByDescending(s => s.Confidence).ToList();
        }
    }

    /// <summary>
    ///     Get statistics for identity tracking.
    /// </summary>
    public IdentityTrackingStats GetStats()
    {
        lock (_lock)
        {
            return new IdentityTrackingStats
            {
                TotalUnifiedIdentities = _identities.Count,
                NamedIdentities = _identities.Count(i => !string.IsNullOrEmpty(i.DisplayName)),
                UnnamedIdentities = _identities.Count(i => string.IsNullOrEmpty(i.DisplayName)),
                VerifiedIdentities = _identities.Count(i => i.IsVerified),
                TotalFaceIdentities = _identities.Sum(i => i.FaceIdentityIds.Count),
                LinkedFaceIdentities = _identities.Where(i => i.FaceIdentityIds.Count > 0)
                    .SelectMany(i => i.FaceIdentityIds).Distinct().Count(),
                TotalSpeakers = _identities.Sum(i => i.SpeakerIds.Count),
                LinkedSpeakers = _identities.Where(i => i.SpeakerIds.Count > 0)
                    .SelectMany(i => i.SpeakerIds).Distinct().Count(),
                TotalDocumentMentions = _identities.Sum(i => i.DocumentMentions.Count),
                LinkedDocumentMentions = _identities.Sum(i => i.DocumentMentions.Count)
            };
        }
    }

    /// <summary>
    ///     Auto-link identities based on name matching.
    ///     Finds face identities with known names that match unified identity names.
    /// </summary>
    public int AutoLinkByName()
    {
        var linkedCount = 0;

        lock (_lock)
        {
            var faceIdentities = _faceTracking.GetAllIdentities();

            foreach (var face in faceIdentities.Where(f => !string.IsNullOrEmpty(f.KnownName)))
            {
                var unified = FindByName(face.KnownName!);
                if (unified != null && !unified.FaceIdentityIds.Contains(face.Id))
                {
                    unified.FaceIdentityIds.Add(face.Id);
                    unified.UpdatedAt = DateTimeOffset.UtcNow;
                    linkedCount++;

                    _logger.LogDebug("Auto-linked face identity {FaceId} to {Name}",
                        face.Id, unified.DisplayName);
                }
            }
        }

        _logger.LogInformation("Auto-linked {Count} face identities by name", linkedCount);
        return linkedCount;
    }

    private void AddLink(Guid identityId, IdentityLink link)
    {
        if (!_links.ContainsKey(identityId)) _links[identityId] = [];
        _links[identityId].Add(link);
    }

    private static string NormalizeName(string name)
    {
        return name.ToLowerInvariant()
            .Replace(".", " ")
            .Replace(",", " ")
            .Replace("-", " ")
            .Trim();
    }

    private static double ComputeNameSimilarity(string name1, string name2)
    {
        if (string.IsNullOrEmpty(name1) || string.IsNullOrEmpty(name2))
            return 0;

        if (name1 == name2)
            return 1.0;

        // Levenshtein distance normalized
        var maxLen = Math.Max(name1.Length, name2.Length);
        if (maxLen == 0)
            return 1.0;

        var distance = LevenshteinDistance(name1, name2);
        return 1.0 - (double)distance / maxLen;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        for (var j = 1; j <= m; j++)
        {
            var cost = t[j - 1] == s[i - 1] ? 0 : 1;
            d[i, j] = Math.Min(
                Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                d[i - 1, j - 1] + cost);
        }

        return d[n, m];
    }
}

/// <summary>
///     Internal link tracking between identity and targets.
/// </summary>
public record IdentityLink
{
    public LinkType LinkType { get; init; }
    public object TargetId { get; init; } = null!;
    public double Confidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum LinkType
{
    FaceToIdentity,
    SpeakerToIdentity,
    DocumentMention,
    TranscriptMention
}

/// <summary>
///     Suggestion for matching an identity.
/// </summary>
public record IdentityMatchSuggestion
{
    public required UnifiedIdentity Identity { get; init; }
    public double Confidence { get; init; }
    public string? MatchedOn { get; init; }
}
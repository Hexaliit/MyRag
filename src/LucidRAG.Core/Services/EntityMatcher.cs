using System.Text.RegularExpressions;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Services;

/// <summary>
///     Generalized entity matching/normalization service for ALL entity types.
///     Supports exact canonical match, alias match, Levenshtein fuzzy match,
///     and optional ONNX embedding similarity matching.
/// </summary>
public interface IEntityMatcher
{
    /// <summary>
    ///     Find an existing entity that matches the given name and type within a collection,
    ///     or return null if no match is found.
    /// </summary>
    Task<ExtractedEntity?> FindMatchAsync(string name, string entityType, Guid collectionId, CancellationToken ct = default);

    /// <summary>
    ///     Normalize a name for the given entity type using type-specific rules.
    /// </summary>
    string Normalize(string name, string entityType);
}

/// <summary>
///     Entity matching implementation with type-specific normalization strategies
///     and a multi-stage matching pipeline: exact → alias → Levenshtein → embedding.
/// </summary>
public partial class EntityMatcher : IEntityMatcher
{
    private readonly RagDocumentsDbContext _db;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ILogger<EntityMatcher> _logger;

    private const double LevenshteinThreshold = 0.8;
    private const double EmbeddingSimilarityThreshold = 0.85;

    public EntityMatcher(
        RagDocumentsDbContext db,
        ILogger<EntityMatcher> logger,
        IEmbeddingService? embeddingService = null)
    {
        _db = db;
        _logger = logger;
        _embeddingService = embeddingService;
    }

    public async Task<ExtractedEntity?> FindMatchAsync(
        string name, string entityType, Guid collectionId, CancellationToken ct = default)
    {
        var normalized = Normalize(name, entityType);

        // Stage 1: Exact canonical match (fast path, DB index)
        var exact = await _db.Entities
            .Where(e => e.EntityType == entityType &&
                        e.CanonicalName.ToLower() == normalized.ToLower())
            .FirstOrDefaultAsync(ct);
        if (exact != null) return exact;

        // Get candidate entities of same type in collection for fuzzy matching
        var candidates = await GetCollectionEntitiesAsync(entityType, collectionId, ct);
        if (candidates.Count == 0) return null;

        // Stage 2: Alias match
        foreach (var candidate in candidates)
        {
            if (candidate.Aliases.Any(a =>
                    string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        // Stage 3: Normalized Levenshtein
        ExtractedEntity? bestLevenshteinMatch = null;
        var bestLevenshteinScore = 0.0;
        foreach (var candidate in candidates)
        {
            var score = NormalizedLevenshtein(normalized, Normalize(candidate.CanonicalName, entityType));
            if (score >= LevenshteinThreshold && score > bestLevenshteinScore)
            {
                bestLevenshteinScore = score;
                bestLevenshteinMatch = candidate;
            }
        }

        if (bestLevenshteinMatch != null) return bestLevenshteinMatch;

        // Stage 4: Embedding similarity (only for person, organization — expensive)
        if (_embeddingService != null && entityType is "author" or "person" or "organization")
        {
            try
            {
                var nameEmbedding = await _embeddingService.EmbedAsync(name, ct);
                ExtractedEntity? bestEmbeddingMatch = null;
                var bestEmbeddingScore = 0.0f;

                // Embed candidates in batch for efficiency
                var candidateNames = candidates.Select(c => c.CanonicalName).ToList();
                var candidateEmbeddings = await _embeddingService.EmbedBatchAsync(candidateNames, ct);

                for (var i = 0; i < candidates.Count; i++)
                {
                    var similarity = CosineSimilarity(nameEmbedding, candidateEmbeddings[i]);
                    if (similarity >= EmbeddingSimilarityThreshold && similarity > bestEmbeddingScore)
                    {
                        bestEmbeddingScore = similarity;
                        bestEmbeddingMatch = candidates[i];
                    }
                }

                if (bestEmbeddingMatch != null) return bestEmbeddingMatch;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Embedding match failed for '{Name}', skipping stage 4", name);
            }
        }

        return null;
    }

    public string Normalize(string name, string entityType)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        return entityType.ToLowerInvariant() switch
        {
            "author" or "person" => NormalizeAuthor(name),
            "organization" => NormalizeOrganization(name),
            "location" => NormalizeLocation(name),
            "concept" or "technology" => NormalizeConcept(name),
            _ => name.Trim().ToLowerInvariant()
        };
    }

    /// <summary>
    ///     Normalize author/person names:
    ///     Strip titles (Dr., Prof.), normalize initials, handle "Last, First" vs "First Last".
    /// </summary>
    internal static string NormalizeAuthor(string name)
    {
        var normalized = name.Trim();

        // Strip common titles/honorifics
        normalized = TitlePrefixRegex().Replace(normalized, "").Trim();
        normalized = TitleSuffixRegex().Replace(normalized, "").Trim();

        // Handle "Last, First" → "First Last"
        if (normalized.Contains(','))
        {
            var parts = normalized.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1]))
                normalized = $"{parts[1]} {parts[0]}";
        }

        // Normalize initials: "J. Smith" → "j smith", "J.R. Smith" → "j r smith"
        normalized = InitialDotRegex().Replace(normalized, "$1 ");

        // Collapse whitespace and lowercase
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim().ToLowerInvariant();

        return normalized;
    }

    /// <summary>
    ///     Normalize organization names:
    ///     Strip common suffixes, handle common abbreviations.
    /// </summary>
    internal static string NormalizeOrganization(string name)
    {
        var normalized = name.Trim();
        normalized = OrgSuffixRegex().Replace(normalized, "").Trim();
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim().ToLowerInvariant();
        return normalized;
    }

    /// <summary>
    ///     Normalize location names:
    ///     Map common abbreviations.
    /// </summary>
    internal static string NormalizeLocation(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();

        // Common US state abbreviations
        normalized = normalized switch
        {
            "ny" => "new york",
            "ca" => "california",
            "tx" => "texas",
            "uk" => "united kingdom",
            "us" or "usa" => "united states",
            _ => normalized
        };

        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    /// <summary>
    ///     Normalize concept/technology names:
    ///     Lowercase, strip version numbers for base matching, handle common acronyms.
    /// </summary>
    internal static string NormalizeConcept(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();

        // Strip version numbers for base matching: "Python 3.11" → "python"
        normalized = VersionSuffixRegex().Replace(normalized, "").Trim();

        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    private async Task<List<ExtractedEntity>> GetCollectionEntitiesAsync(
        string entityType, Guid collectionId, CancellationToken ct)
    {
        return await _db.DocumentEntityLinks
            .Where(del => _db.Documents
                .Where(d => d.CollectionId == collectionId)
                .Select(d => d.Id)
                .Contains(del.DocumentId))
            .Select(del => del.Entity!)
            .Where(e => e.EntityType == entityType)
            .Distinct()
            .ToListAsync(ct);
    }

    internal static double NormalizedLevenshtein(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1.0;

        var m = a.Length;
        var n = b.Length;
        if (m == 0 || n == 0) return 0.0;

        var d = new int[m + 1, n + 1];
        for (var i = 0; i <= m; i++) d[i, 0] = i;
        for (var j = 0; j <= n; j++) d[0, j] = j;
        for (var i = 1; i <= m; i++)
        for (var j = 1; j <= n; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[m, n] / Math.Max(m, n);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-10f);
    }

    // Regex patterns for normalization

    [GeneratedRegex(@"^(Dr\.?|Prof\.?|Professor|Mr\.?|Mrs\.?|Ms\.?|Sir|Dame)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePrefixRegex();

    [GeneratedRegex(@",?\s*(Ph\.?D\.?|M\.?D\.?|Jr\.?|Sr\.?|III|II|IV)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleSuffixRegex();

    [GeneratedRegex(@"([A-Za-z])\.(?=\s|$|[A-Z])")]
    private static partial Regex InitialDotRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@",?\s*(Inc\.?|Ltd\.?|Corp\.?|LLC|GmbH|S\.?A\.?|Co\.?|PLC|AG|University\s+of)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrgSuffixRegex();

    [GeneratedRegex(@"\s+v?\d+[\.\d]*\s*$")]
    private static partial Regex VersionSuffixRegex();
}

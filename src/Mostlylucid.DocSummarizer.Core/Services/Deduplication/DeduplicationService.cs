using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services.Deduplication;

/// <summary>
///     Service for deduplicating segments during ingestion and retrieval.
///     Implements the two-phase deduplication strategy:
///     - Ingestion: Intra-document deduplication with near-duplicate salience boosting
///     - Retrieval: Cross-document deduplication post-RRF ranking
/// </summary>
public class DeduplicationService : IDeduplicationService
{
    private readonly DeduplicationConfig _config;
    private readonly ILogger<DeduplicationService> _logger;

    public DeduplicationService(
        IOptions<DeduplicationConfig> config,
        ILogger<DeduplicationService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Deduplicate segments during ingestion (intra-document only).
    ///     Near-duplicates boost salience; exact duplicates are dropped.
    /// </summary>
    public DeduplicationResult<Segment> DeduplicateForIngestion(
        List<Segment> segments,
        string? documentId = null)
    {
        if (!_config.Ingestion.Enabled || segments.Count <= 1)
            return new DeduplicationResult<Segment>(
                segments,
                segments.Count,
                segments.Count,
                0,
                0.0,
                0.0,
                TimeSpan.Zero,
                documentId);

        var startTime = DateTime.UtcNow;
        var config = _config.Ingestion;

        // Filter by minimum salience (skip very low-value segments)
        var candidates = segments
            .Where(s => s.SalienceScore >= config.SalienceThreshold || s.SalienceScore == 0) // 0 = unscored, keep
            .OrderByDescending(s => s.SalienceScore)
            .ToList();

        var selected = new List<Segment>();
        var nearDuplicateCounts = new Dictionary<int, int>(); // index in selected -> count of near-dupes
        var exactDuplicatesDropped = 0;
        var nearDuplicatesMerged = 0;

        foreach (var segment in candidates)
        {
            if (segment.Embedding == null || segment.Embedding.Length == 0)
            {
                // No embedding - can't check similarity, keep it
                selected.Add(segment);
                nearDuplicateCounts[selected.Count - 1] = 0;
                continue;
            }

            // Check if too similar to any already-selected segment
            int? matchedIndex = null;
            var isExactDuplicate = false;

            for (var i = 0; i < selected.Count; i++)
            {
                var existing = selected[i];
                if (existing.Embedding == null || existing.Embedding.Length == 0)
                    continue;

                var similarity = CosineSimilarity(segment.Embedding, existing.Embedding);
                if (similarity >= config.SimilarityThreshold)
                {
                    matchedIndex = i;
                    // Check if exact duplicate (same content hash)
                    isExactDuplicate = !string.IsNullOrEmpty(segment.ContentHash) &&
                                       segment.ContentHash == existing.ContentHash;
                    break;
                }
            }

            if (matchedIndex.HasValue)
            {
                if (isExactDuplicate)
                {
                    // Exact duplicate - drop silently, no boost (likely boilerplate)
                    exactDuplicatesDropped++;
                }
                else
                {
                    // Near-duplicate - track for salience boost
                    nearDuplicateCounts[matchedIndex.Value]++;
                    nearDuplicatesMerged++;
                }
            }
            else
            {
                // No match - add to selected
                selected.Add(segment);
                nearDuplicateCounts[selected.Count - 1] = 0;
            }
        }

        // Apply salience boosts for near-duplicates
        var maxBoostApplied = 0.0;
        if (config.EnableSalienceBoost)
            for (var i = 0; i < selected.Count; i++)
            {
                var count = nearDuplicateCounts.GetValueOrDefault(i, 0);
                if (count > 0)
                {
                    var boost = CalculateBoost(count, config);
                    var originalSalience = selected[i].SalienceScore;
                    selected[i].SalienceScore *= 1.0 + boost;
                    selected[i].SalienceScore = Math.Min(selected[i].SalienceScore, config.MaxSalienceBoost);

                    var appliedBoost = selected[i].SalienceScore - originalSalience;
                    maxBoostApplied = Math.Max(maxBoostApplied, appliedBoost);
                }
            }

        var elapsed = DateTime.UtcNow - startTime;
        var dedupRatio = segments.Count > 0
            ? (double)(segments.Count - selected.Count) / segments.Count
            : 0.0;

        // Log results
        if (_config.Analytics.EnableLogging)
        {
            _logger.LogDebug(
                "Ingestion dedup ({DocumentId}): {Before} → {After} segments ({Ratio:P1} removed, {NearDupes} near-dupes boosted, {ExactDupes} exact dropped)",
                documentId ?? "unknown",
                segments.Count,
                selected.Count,
                dedupRatio,
                nearDuplicatesMerged,
                exactDuplicatesDropped);

            // Warn if dedup ratio is suspiciously high
            if (dedupRatio > _config.Analytics.HighIngestionDedupThreshold)
                _logger.LogWarning(
                    "High ingestion dedup ratio ({Ratio:P1}) for document {DocumentId}. May indicate excessive boilerplate or auto-generated content.",
                    dedupRatio,
                    documentId ?? "unknown");

            // Warn if max boost is high
            if (maxBoostApplied > _config.Analytics.HighSalienceBoostThreshold)
                _logger.LogWarning(
                    "High salience boost ({Boost:F2}) applied in document {DocumentId}. Verify content is not spam.",
                    maxBoostApplied,
                    documentId ?? "unknown");
        }

        return new DeduplicationResult<Segment>(
            selected,
            segments.Count,
            selected.Count,
            nearDuplicatesMerged + exactDuplicatesDropped,
            maxBoostApplied,
            dedupRatio,
            elapsed,
            documentId);
    }

    /// <summary>
    ///     Deduplicate segments during retrieval (cross-document, post-RRF).
    ///     Keeps the highest-scoring segment when duplicates are found.
    /// </summary>
    public DeduplicationResult<T> DeduplicateForRetrieval<T>(
        List<T> rankedResults,
        Func<T, float[]?> getEmbedding,
        string? queryId = null) where T : class
    {
        if (!_config.Retrieval.Enabled || rankedResults.Count <= 1)
            return new DeduplicationResult<T>(
                rankedResults,
                rankedResults.Count,
                rankedResults.Count,
                0,
                0.0,
                0.0,
                TimeSpan.Zero,
                queryId);

        var startTime = DateTime.UtcNow;
        var config = _config.Retrieval;

        var selected = new List<T>();
        var duplicatesRemoved = 0;

        foreach (var result in rankedResults)
        {
            var embedding = getEmbedding(result);
            if (embedding == null || embedding.Length == 0)
            {
                // No embedding - can't check similarity, keep it
                selected.Add(result);
                continue;
            }

            // Check if too similar to any already-selected segment
            var isDuplicate = false;
            foreach (var existing in selected)
            {
                var existingEmbedding = getEmbedding(existing);
                if (existingEmbedding == null || existingEmbedding.Length == 0)
                    continue;

                var similarity = CosineSimilarity(embedding, existingEmbedding);
                if (similarity >= config.SimilarityThreshold)
                {
                    isDuplicate = true;
                    duplicatesRemoved++;
                    break;
                }
            }

            if (!isDuplicate) selected.Add(result);
        }

        var elapsed = DateTime.UtcNow - startTime;
        var dedupRatio = rankedResults.Count > 0
            ? (double)(rankedResults.Count - selected.Count) / rankedResults.Count
            : 0.0;

        // Log results
        if (_config.Analytics.EnableLogging)
        {
            _logger.LogDebug(
                "Retrieval dedup: {Before} → {After} segments ({Ratio:P1} removed, {DuplicatesRemoved} duplicates)",
                rankedResults.Count,
                selected.Count,
                dedupRatio,
                duplicatesRemoved);

            // Warn if dedup ratio is suspiciously high
            if (dedupRatio > _config.Analytics.HighRetrievalDedupThreshold)
                _logger.LogWarning(
                    "High retrieval dedup ratio ({Ratio:P1}). Query may be too broad or corpus has many similar documents.",
                    dedupRatio);
        }

        return new DeduplicationResult<T>(
            selected,
            rankedResults.Count,
            selected.Count,
            duplicatesRemoved,
            0.0,
            dedupRatio,
            elapsed,
            queryId);
    }

    /// <summary>
    ///     Calculate the salience boost for a segment with near-duplicates.
    /// </summary>
    private static double CalculateBoost(int nearDuplicateCount, IngestionDeduplicationConfig config)
    {
        if (nearDuplicateCount <= 0)
            return 0.0;

        return config.BoostDecayMode switch
        {
            BoostDecayMode.Linear => config.BoostPerNearDuplicate * nearDuplicateCount,
            BoostDecayMode.Logarithmic => config.BoostPerNearDuplicate *
                                          Math.Log(1 + nearDuplicateCount, config.LogBase),
            _ => config.BoostPerNearDuplicate * nearDuplicateCount
        };
    }

    /// <summary>
    ///     Calculate cosine similarity between two embedding vectors.
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0.0;
    }
}

/// <summary>
///     Result of a deduplication operation with metrics.
/// </summary>
/// <typeparam name="T">Type of items being deduplicated</typeparam>
public record DeduplicationResult<T>(
    List<T> Items,
    int OriginalCount,
    int FinalCount,
    int DuplicatesRemoved,
    double MaxBoostApplied,
    double DeduplicationRatio,
    TimeSpan ProcessingTime,
    string? ContextId = null)
{
    /// <summary>
    ///     Number of items that were deduplicated (removed).
    /// </summary>
    public int ItemsDeduplicated => OriginalCount - FinalCount;

    /// <summary>
    ///     Percentage of items that were deduplicated.
    /// </summary>
    public double DeduplicationPercentage => OriginalCount > 0
        ? (double)ItemsDeduplicated / OriginalCount * 100
        : 0.0;
}
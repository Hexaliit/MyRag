using Mostlylucid.DocSummarizer.Models;

namespace LucidRAG.Core.Services;

/// <summary>
/// Selects which segments to store as evidence artifacts.
/// Optimizes for coverage while minimizing storage by:
/// 1. Filtering by salience threshold
/// 2. Deduplicating semantically similar segments
/// 3. Prioritizing segments with unique features/entities
/// </summary>
public class SegmentSelector
{
    private readonly double _salienceThreshold;
    private readonly double _similarityThreshold;
    private readonly int _maxSegmentsPerDocument;

    /// <summary>
    /// Creates a segment selector with configurable thresholds.
    /// </summary>
    /// <param name="salienceThreshold">Minimum salience score to consider (0-1). Default 0.1</param>
    /// <param name="similarityThreshold">Max cosine similarity before deduping (0-1). Default 0.92</param>
    /// <param name="maxSegmentsPerDocument">Maximum segments to store per document. Default 200</param>
    public SegmentSelector(
        double salienceThreshold = 0.1,
        double similarityThreshold = 0.92,
        int maxSegmentsPerDocument = 200)
    {
        _salienceThreshold = salienceThreshold;
        _similarityThreshold = similarityThreshold;
        _maxSegmentsPerDocument = maxSegmentsPerDocument;
    }

    /// <summary>
    /// Select segments for evidence storage using salience, deduplication, and coverage.
    /// </summary>
    public List<Segment> SelectForEvidence(IReadOnlyList<Segment> segments)
    {
        if (segments.Count == 0)
            return new List<Segment>();

        // Step 1: Filter by salience threshold
        // Handle case where all segments have 0 salience (plain text without scoring)
        var maxSalience = segments.Max(s => s.SalienceScore);
        var effectiveThreshold = maxSalience > 0 ? _salienceThreshold : 0;

        var salientSegments = segments
            .Where(s => s.SalienceScore >= effectiveThreshold)
            .OrderByDescending(s => s.SalienceScore)
            .ToList();

        // Ensure minimum coverage: at least 20% of segments or 50, whichever is larger
        var minSegments = Math.Max(50, segments.Count / 5);
        if (salientSegments.Count < minSegments)
        {
            // Take top segments by salience if threshold was too aggressive
            salientSegments = segments
                .OrderByDescending(s => s.SalienceScore)
                .Take(minSegments)
                .ToList();
        }

        Console.WriteLine($"[SegmentSelector] Step 1: After salience filter: {salientSegments.Count} (min={minSegments}, maxSalience={maxSalience:F4})");

        // Step 2: Semantic deduplication
        var dedupedSegments = DeduplicateBySimilarity(salientSegments);
        Console.WriteLine($"[SegmentSelector] Step 2: After deduplication: {dedupedSegments.Count}");

        // Step 2.5: Ensure minimum coverage even if deduplication was too aggressive
        // Fall back to evenly-sampled segments from the original pool
        if (dedupedSegments.Count < minSegments)
        {
            Console.WriteLine($"[SegmentSelector] Step 2.5: Deduplication too aggressive, adding evenly-sampled segments to meet min={minSegments}");
            var existingIds = new HashSet<int>(dedupedSegments.Select(s => s.Index));
            var orderedByPosition = salientSegments
                .Where(s => !existingIds.Contains(s.Index))
                .OrderBy(s => s.Index)
                .ToList();

            // Sample evenly across the document to meet minimum
            var needed = minSegments - dedupedSegments.Count;
            var step = Math.Max(1, orderedByPosition.Count / needed);

            for (int i = 0; i < orderedByPosition.Count && dedupedSegments.Count < minSegments; i += step)
            {
                dedupedSegments.Add(orderedByPosition[i]);
            }
            Console.WriteLine($"[SegmentSelector] Step 2.5: After fallback sampling: {dedupedSegments.Count}");
        }

        // Step 3: Coverage-based selection (prioritize unique entities)
        var selectedSegments = SelectForCoverage(dedupedSegments);
        Console.WriteLine($"[SegmentSelector] Step 3: After coverage: {selectedSegments.Count}");

        // Step 4: Apply max limit
        if (selectedSegments.Count > _maxSegmentsPerDocument)
        {
            selectedSegments = selectedSegments
                .Take(_maxSegmentsPerDocument)
                .ToList();
        }

        Console.WriteLine($"[SegmentSelector] Final: {selectedSegments.Count}/{segments.Count} segments");
        return selectedSegments;
    }

    /// <summary>
    /// Remove segments that are too similar to already-selected ones.
    /// Uses greedy selection: pick highest salience, then skip similar ones.
    /// </summary>
    private List<Segment> DeduplicateBySimilarity(List<Segment> segments)
    {
        if (segments.Count <= 1)
            return segments;

        var selected = new List<Segment>();
        var selectedEmbeddings = new List<float[]>();

        foreach (var segment in segments)
        {
            // Skip if no embedding
            if (segment.Embedding == null || segment.Embedding.Length == 0)
            {
                selected.Add(segment);
                continue;
            }

            // Check similarity against all selected segments
            var isTooSimilar = false;
            foreach (var existingEmbedding in selectedEmbeddings)
            {
                var similarity = CosineSimilarity(segment.Embedding, existingEmbedding);
                if (similarity >= _similarityThreshold)
                {
                    isTooSimilar = true;
                    break;
                }
            }

            if (!isTooSimilar)
            {
                selected.Add(segment);
                selectedEmbeddings.Add(segment.Embedding);
            }
        }

        return selected;
    }

    /// <summary>
    /// Prioritize segments that contribute unique features while maintaining minimum coverage.
    /// For structured documents: prioritizes segments with distinct headings/sections.
    /// For plain text: ensures even distribution across the document.
    /// </summary>
    private List<Segment> SelectForCoverage(List<Segment> segments)
    {
        if (segments.Count <= 1)
            return segments;

        // Track which features we've covered
        var coveredFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Segment>();

        // First pass: segments with unique features (not yet covered)
        foreach (var segment in segments)
        {
            var features = ExtractEntities(segment);
            var newFeatures = features.Where(f => !coveredFeatures.Contains(f)).ToList();

            if (newFeatures.Count > 0)
            {
                result.Add(segment);
                foreach (var feature in features)
                    coveredFeatures.Add(feature);
            }
        }

        // Ensure minimum coverage - if we have very few unique features,
        // take evenly distributed samples across the document
        var minCoverage = Math.Max(50, segments.Count / 5); // At least 20% or 50
        if (result.Count < minCoverage)
        {
            // Sort by position (index) to get even distribution
            var orderedSegments = segments
                .Where(s => !result.Contains(s))
                .OrderBy(s => s.Index)
                .ToList();

            // Sample evenly across the document
            var needed = minCoverage - result.Count;
            var step = Math.Max(1, orderedSegments.Count / needed);

            for (int i = 0; i < orderedSegments.Count && result.Count < minCoverage; i += step)
            {
                result.Add(orderedSegments[i]);
            }
        }

        // Cap at maximum
        if (result.Count > _maxSegmentsPerDocument)
        {
            result = result.Take(_maxSegmentsPerDocument).ToList();
        }

        return result;
    }

    /// <summary>
    /// Extract features from segment for coverage tracking.
    /// Uses heading, section title, and segment type as features.
    /// </summary>
    private static List<string> ExtractEntities(Segment segment)
    {
        var features = new List<string>();

        // Use section title as a feature
        if (!string.IsNullOrWhiteSpace(segment.SectionTitle))
        {
            features.Add($"section:{segment.SectionTitle}");
        }

        // Use heading path for coverage (each level is a feature)
        if (!string.IsNullOrWhiteSpace(segment.HeadingPath))
        {
            var parts = segment.HeadingPath.Split('>');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    features.Add($"heading:{trimmed}");
            }
        }

        // Segment type as feature (ensure coverage of different content types)
        features.Add($"type:{segment.Type}");

        // Page number as feature (for PDF coverage)
        if (segment.PageNumber.HasValue)
        {
            features.Add($"page:{segment.PageNumber}");
        }

        return features;
    }

    /// <summary>
    /// Compute cosine similarity between two embedding vectors.
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dotProduct / denominator;
    }
}

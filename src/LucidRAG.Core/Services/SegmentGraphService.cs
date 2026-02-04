using System.Text.Json;
using LucidRAG.Data;
using LucidRAG.Entities;
using Mostlylucid.DocSummarizer.Models;

namespace LucidRAG.Services;

/// <summary>
///     Builds and queries intra-document segment graphs.
///     Links segments by: sequential order, heading hierarchy, semantic similarity, shared entities.
/// </summary>
public class SegmentGraphService(
    RagDocumentsDbContext db,
    ILogger<SegmentGraphService> logger) : ISegmentGraphService
{
    /// <summary>
    ///     Build segment graph for a document from its segments.
    ///     Creates links based on multiple signals.
    /// </summary>
    public async Task<int> BuildGraphAsync(
        Guid documentId,
        IReadOnlyList<Segment> segments,
        CancellationToken ct = default)
    {
        if (segments.Count < 2)
            return 0;

        logger.LogInformation("Building segment graph for document {DocumentId} with {Count} segments",
            documentId, segments.Count);

        // Clear existing links for this document
        var existingLinks = await db.Set<SegmentLink>()
            .Where(l => l.DocumentId == documentId)
            .ToListAsync(ct);
        db.Set<SegmentLink>().RemoveRange(existingLinks);

        var links = new List<SegmentLink>();

        // 1. Sequential links (adjacent segments)
        links.AddRange(BuildSequentialLinks(documentId, segments));

        // 2. Heading hierarchy links (same section)
        links.AddRange(BuildHeadingLinks(documentId, segments));

        // 3. Semantic similarity links (high cosine similarity)
        links.AddRange(BuildSemanticLinks(documentId, segments));

        // 4. Entity co-occurrence links (shared entity mentions)
        links.AddRange(BuildEntityLinks(documentId, segments));

        // Deduplicate links (keep highest weight)
        var deduped = links
            .GroupBy(l => (l.SourceSegmentHash, l.TargetSegmentHash))
            .Select(g => g.OrderByDescending(l => l.Weight).First())
            .ToList();

        db.Set<SegmentLink>().AddRange(deduped);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created {Count} segment links for document {DocumentId}",
            deduped.Count, documentId);

        return deduped.Count;
    }

    /// <summary>
    ///     Get connected segments for a given segment hash.
    ///     Returns segments linked by any type, ordered by weight.
    /// </summary>
    public async Task<List<ConnectedSegment>> GetConnectedSegmentsAsync(
        string segmentHash,
        int maxResults = 10,
        string[]? linkTypes = null,
        CancellationToken ct = default)
    {
        var query = db.Set<SegmentLink>()
            .Where(l => l.SourceSegmentHash == segmentHash || l.TargetSegmentHash == segmentHash);

        if (linkTypes != null && linkTypes.Length > 0) query = query.Where(l => linkTypes.Contains(l.LinkType));

        var links = await query
            .OrderByDescending(l => l.Weight)
            .Take(maxResults)
            .ToListAsync(ct);

        return links.Select(l => new ConnectedSegment(
            l.SourceSegmentHash == segmentHash ? l.TargetSegmentHash : l.SourceSegmentHash,
            l.LinkType,
            l.Weight,
            l.DocumentId
        )).ToList();
    }

    /// <summary>
    ///     Expand retrieval results using the segment graph.
    ///     For each retrieved segment, include highly-connected segments.
    /// </summary>
    public async Task<List<string>> ExpandRetrievalAsync(
        IEnumerable<string> segmentHashes,
        int expansionDepth = 1,
        double minWeight = 0.5,
        int maxExpansions = 5,
        CancellationToken ct = default)
    {
        var hashSet = segmentHashes.ToHashSet();
        var expanded = new HashSet<string>(hashSet);

        for (var depth = 0; depth < expansionDepth; depth++)
        {
            var toExpand = expanded.ToList();
            var newHashes = new List<string>();

            foreach (var hash in toExpand)
            {
                var connected = await GetConnectedSegmentsAsync(hash, maxExpansions, ct: ct);
                var filtered = connected
                    .Where(c => c.Weight >= minWeight && !expanded.Contains(c.SegmentHash))
                    .Take(maxExpansions);

                newHashes.AddRange(filtered.Select(c => c.SegmentHash));
            }

            foreach (var hash in newHashes) expanded.Add(hash);
        }

        return expanded.Except(hashSet).ToList();
    }

    /// <summary>
    ///     Calculate graph-based salience for segments.
    ///     Segments with more/stronger connections are more salient.
    /// </summary>
    public async Task<Dictionary<string, double>> CalculateGraphSalienceAsync(
        Guid documentId,
        CancellationToken ct = default)
    {
        var links = await db.Set<SegmentLink>()
            .Where(l => l.DocumentId == documentId)
            .ToListAsync(ct);

        // Calculate weighted degree centrality
        var salience = new Dictionary<string, double>();

        foreach (var link in links)
        {
            if (!salience.ContainsKey(link.SourceSegmentHash))
                salience[link.SourceSegmentHash] = 0;
            if (!salience.ContainsKey(link.TargetSegmentHash))
                salience[link.TargetSegmentHash] = 0;

            salience[link.SourceSegmentHash] += link.Weight;
            salience[link.TargetSegmentHash] += link.Weight;
        }

        // Normalize to 0-1
        if (salience.Count > 0)
        {
            var maxSalience = salience.Values.Max();
            if (maxSalience > 0)
                foreach (var key in salience.Keys.ToList())
                    salience[key] /= maxSalience;
        }

        return salience;
    }

    /// <summary>
    ///     Get graph statistics for a document.
    /// </summary>
    public async Task<SegmentGraphStats> GetStatsAsync(
        Guid documentId,
        CancellationToken ct = default)
    {
        var links = await db.Set<SegmentLink>()
            .Where(l => l.DocumentId == documentId)
            .ToListAsync(ct);

        var typeDistribution = links
            .GroupBy(l => l.LinkType)
            .ToDictionary(g => g.Key, g => g.Count());

        var uniqueSegments = links
            .SelectMany(l => new[] { l.SourceSegmentHash, l.TargetSegmentHash })
            .Distinct()
            .Count();

        return new SegmentGraphStats(
            links.Count,
            uniqueSegments,
            links.Count > 0 ? links.Average(l => l.Weight) : 0,
            typeDistribution);
    }

    private List<SegmentLink> BuildSequentialLinks(Guid documentId, IReadOnlyList<Segment> segments)
    {
        var links = new List<SegmentLink>();

        for (var i = 0; i < segments.Count - 1; i++)
        {
            var current = segments[i];
            var next = segments[i + 1];

            if (string.IsNullOrEmpty(current.ContentHash) || string.IsNullOrEmpty(next.ContentHash))
                continue;

            links.Add(new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                SourceSegmentHash = current.ContentHash,
                TargetSegmentHash = next.ContentHash,
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 0.7 // Sequential is moderately strong
            });
        }

        return links;
    }

    private List<SegmentLink> BuildHeadingLinks(Guid documentId, IReadOnlyList<Segment> segments)
    {
        var links = new List<SegmentLink>();

        // Group segments by heading path
        var byHeading = segments
            .Where(s => !string.IsNullOrEmpty(s.HeadingPath) && !string.IsNullOrEmpty(s.ContentHash))
            .GroupBy(s => s.HeadingPath)
            .Where(g => g.Count() > 1);

        foreach (var group in byHeading)
        {
            var segmentsInGroup = group.ToList();

            // Link all pairs within same heading (fully connected)
            for (var i = 0; i < segmentsInGroup.Count; i++)
            for (var j = i + 1; j < segmentsInGroup.Count; j++)
                links.Add(new SegmentLink
                {
                    Id = Guid.NewGuid(),
                    DocumentId = documentId,
                    SourceSegmentHash = segmentsInGroup[i].ContentHash!,
                    TargetSegmentHash = segmentsInGroup[j].ContentHash!,
                    LinkType = SegmentLinkTypes.Heading,
                    Weight = 0.8, // Same heading is strong signal
                    Metadata = JsonSerializer.Serialize(new { heading = group.Key })
                });
        }

        return links;
    }

    private List<SegmentLink> BuildSemanticLinks(Guid documentId, IReadOnlyList<Segment> segments)
    {
        var links = new List<SegmentLink>();
        const double semanticThreshold = 0.85; // High threshold for semantic links

        var segmentsWithEmbeddings = segments
            .Where(s => s.Embedding != null && s.Embedding.Length > 0 && !string.IsNullOrEmpty(s.ContentHash))
            .ToList();

        // Compare all pairs (O(n^2) but limited by segment count)
        for (var i = 0; i < segmentsWithEmbeddings.Count; i++)
        for (var j = i + 1; j < segmentsWithEmbeddings.Count; j++)
        {
            var similarity = CosineSimilarity(
                segmentsWithEmbeddings[i].Embedding!,
                segmentsWithEmbeddings[j].Embedding!);

            if (similarity >= semanticThreshold)
                links.Add(new SegmentLink
                {
                    Id = Guid.NewGuid(),
                    DocumentId = documentId,
                    SourceSegmentHash = segmentsWithEmbeddings[i].ContentHash!,
                    TargetSegmentHash = segmentsWithEmbeddings[j].ContentHash!,
                    LinkType = SegmentLinkTypes.Semantic,
                    Weight = similarity,
                    Metadata = JsonSerializer.Serialize(new { similarity })
                });
        }

        return links;
    }

    private List<SegmentLink> BuildEntityLinks(Guid documentId, IReadOnlyList<Segment> segments)
    {
        // TODO: Extract entities from segment text and link by shared entities
        // For now, return empty - can be enhanced with NER integration
        return [];
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dotProduct = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dotProduct / denominator;
    }
}

public interface ISegmentGraphService
{
    Task<int> BuildGraphAsync(Guid documentId, IReadOnlyList<Segment> segments, CancellationToken ct = default);

    Task<List<ConnectedSegment>> GetConnectedSegmentsAsync(string segmentHash, int maxResults = 10,
        string[]? linkTypes = null, CancellationToken ct = default);

    Task<List<string>> ExpandRetrievalAsync(IEnumerable<string> segmentHashes, int expansionDepth = 1,
        double minWeight = 0.5, int maxExpansions = 5, CancellationToken ct = default);

    Task<Dictionary<string, double>> CalculateGraphSalienceAsync(Guid documentId, CancellationToken ct = default);
    Task<SegmentGraphStats> GetStatsAsync(Guid documentId, CancellationToken ct = default);
}

public record ConnectedSegment(string SegmentHash, string LinkType, double Weight, Guid DocumentId);

public record SegmentGraphStats(
    int TotalLinks,
    int UniqueSegments,
    double AverageWeight,
    Dictionary<string, int> TypeDistribution);
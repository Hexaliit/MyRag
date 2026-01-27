using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 1: Evidence Preparation (Deterministic).
/// Takes analyzed items + content items, runs ArticleProcessor to extract segments,
/// builds EvidenceCorpus with embeddings, URL whitelist, and entity registry.
/// </summary>
public static class EvidencePreparationService
{
    // Regex for capitalized multi-word phrases (entity extraction)
    private static readonly Regex EntityRegex = new(
        @"\b([A-Z][a-z]+(?:\s+(?:[A-Z][a-z]+|of|the|and|for|in|on|at|to|de|von|van))*(?:\s+[A-Z][a-z]+)+)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Build the evidence corpus from processed articles.
    /// </summary>
    public static EvidenceCorpus BuildCorpus(
        List<ProcessedArticle> processedArticles,
        List<ContentItem> contentItems,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems)
    {
        var segments = new List<EvidenceSegment>();
        var articles = new Dictionary<string, ArticleSummary>();
        var urlFetchDates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var knownTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSourceUrls = new List<string>();

        // Build lookup for relevance scores
        var relevanceByUrl = analyzedItems
            .Where(a => !string.IsNullOrEmpty(a.url))
            .GroupBy(a => a.url)
            .ToDictionary(g => g.Key, g => g.First().relevance, StringComparer.OrdinalIgnoreCase);

        var relevanceByTitle = analyzedItems
            .GroupBy(a => a.title)
            .ToDictionary(g => g.Key, g => g.First().relevance, StringComparer.OrdinalIgnoreCase);

        foreach (var article in processedArticles)
        {
            var item = article.Item;
            var url = item.Url ?? "";
            var title = item.Title;

            // Get relevance from analyzed items
            var relevance = 0.0;
            if (!string.IsNullOrEmpty(url) && relevanceByUrl.TryGetValue(url, out var rUrl))
                relevance = rUrl;
            else if (relevanceByTitle.TryGetValue(title, out var rTitle))
                relevance = rTitle;

            // Register article
            if (!string.IsNullOrEmpty(url))
            {
                urlFetchDates[url] = item.FetchedAt;
                allSourceUrls.Add(url);
            }
            knownTitles.Add(title);

            articles[url.Length > 0 ? url : title] = new ArticleSummary
            {
                Title = title,
                Url = url,
                Summary = item.Summary ?? "",
                Relevance = relevance,
                SegmentCount = article.TopSegments.Count,
                FetchedAt = item.FetchedAt
            };

            // Flatten segments into corpus
            foreach (var seg in article.TopSegments)
            {
                if (seg.Embedding == null) continue; // skip segments without embeddings

                segments.Add(new EvidenceSegment
                {
                    Segment = seg,
                    ArticleTitle = title,
                    ArticleUrl = url,
                    ArticleRelevance = relevance,
                    FetchedAt = item.FetchedAt
                });
            }
        }

        // Compute global centroid (mean of all segment embeddings)
        float[]? centroid = null;
        if (segments.Count > 0)
        {
            var dim = segments[0].Segment.Embedding!.Length;
            centroid = new float[dim];
            foreach (var seg in segments)
            {
                var emb = seg.Segment.Embedding!;
                for (var i = 0; i < dim; i++)
                    centroid[i] += emb[i];
            }
            for (var i = 0; i < dim; i++)
                centroid[i] /= segments.Count;

            // L2 normalize
            var norm = MathF.Sqrt(centroid.Sum(x => x * x));
            if (norm > 0)
                for (var i = 0; i < dim; i++)
                    centroid[i] /= norm;
        }

        // Extract entities from all segment text
        var entityCounts = new Dictionary<string, TrackedEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments)
        {
            var matches = EntityRegex.Matches(seg.Segment.Text);
            foreach (Match match in matches)
            {
                var name = match.Value.Trim();
                if (name.Length < 3) continue;
                var normalized = name.ToLowerInvariant();

                if (!entityCounts.TryGetValue(normalized, out var entity))
                {
                    entity = new TrackedEntity
                    {
                        Name = name,
                        NormalizedName = normalized,
                        SourceArticles = []
                    };
                    entityCounts[normalized] = entity;
                }
                entity.MentionCount++;
                if (!entity.SourceArticles.Contains(seg.ArticleTitle))
                    entity.SourceArticles.Add(seg.ArticleTitle);
            }
        }

        // Also add entity names from analyzedItems topics
        foreach (var item in analyzedItems)
        {
            if (!string.IsNullOrEmpty(item.topic))
            {
                var normalized = item.topic.ToLowerInvariant();
                entityCounts.TryAdd(normalized, new TrackedEntity
                {
                    Name = item.topic,
                    NormalizedName = normalized,
                    SourceArticles = [item.title]
                });
            }
        }

        var knownEntityNames = new HashSet<string>(
            entityCounts.Keys, StringComparer.OrdinalIgnoreCase);

        return new EvidenceCorpus
        {
            Segments = segments,
            GlobalCentroid = centroid,
            Articles = articles,
            AllSourceUrls = allSourceUrls.Distinct().ToList(),
            GlobalEntities = entityCounts.Values
                .OrderByDescending(e => e.MentionCount)
                .ToList(),
            UrlFetchDates = urlFetchDates,
            KnownEntityNames = knownEntityNames,
            KnownTitles = knownTitles
        };
    }
}

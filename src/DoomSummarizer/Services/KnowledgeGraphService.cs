using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Self-assembling knowledge graph backed by DuckDB with HNSW vector search.
/// Accumulates entities, relationships, and provenance across runs.
/// Uses freshness-weighted scoring so recent information surfaces first.
/// </summary>
public class KnowledgeGraphService
{
    private readonly DuckDbVectorStore _vectorStore;

    public KnowledgeGraphService(DuckDbVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <summary>
    /// Index item embeddings into DuckDB for HNSW similarity search.
    /// </summary>
    public async Task IndexItemEmbeddingsAsync(
        List<ContentItem> items,
        CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Embedding == null) continue;

            await _vectorStore.UpsertItemEmbeddingAsync(
                item.Id, item.Title, item.Source, item.Url, item.Embedding);
        }
    }

    /// <summary>
    /// Ingest NER entities from analyzed articles into the knowledge graph.
    /// Builds entity nodes, article→entity mentions, and co-occurrence edges.
    /// </summary>
    public async Task IngestEntitiesAsync(
        List<(ContentItem item, List<NerEntity> entities)> articleEntities,
        CancellationToken ct = default)
    {
        foreach (var (item, entities) in articleEntities)
        {
            ct.ThrowIfCancellationRequested();

            // Ensure the item is in the vector store (for article provenance lookups)
            if (item.Embedding != null)
            {
                await _vectorStore.UpsertItemEmbeddingAsync(
                    item.Id, item.Title, item.Source, item.Url, item.Embedding);
            }

            // Deduplicate entities within this article (case-insensitive)
            var deduped = entities
                .GroupBy(e => e.Text.ToLowerInvariant())
                .Select(g => g.OrderByDescending(e => e.Confidence).First())
                .ToList();

            // Upsert each entity and record the mention
            var entityIds = new List<string>();
            foreach (var entity in deduped)
            {
                var entityId = GenerateEntityId(entity.Text, entity.Type);
                entityIds.Add(entityId);

                await _vectorStore.UpsertEntityAsync(entityId, entity.Text, entity.Type, entity.Confidence);
                await _vectorStore.UpsertEntityMentionAsync(entityId, item.Id, entity.Confidence,
                    TruncateContext(item.Title, 200));
            }

            // Build co-occurrence relationships (entities in the same article)
            for (var i = 0; i < entityIds.Count; i++)
            {
                for (var j = i + 1; j < entityIds.Count; j++)
                {
                    await _vectorStore.UpsertRelationshipAsync(entityIds[i], entityIds[j]);
                }
            }
        }
    }

    /// <summary>
    /// Ingest linked page entities with lower confidence (one hop away).
    /// </summary>
    public async Task IngestLinkedPageEntitiesAsync(
        ContentItem parentItem,
        List<NerEntity> linkedEntities,
        string linkedUrl,
        CancellationToken ct = default)
    {
        var deduped = linkedEntities
            .GroupBy(e => e.Text.ToLowerInvariant())
            .Select(g => g.OrderByDescending(e => e.Confidence).First())
            .ToList();

        foreach (var entity in deduped)
        {
            ct.ThrowIfCancellationRequested();
            var entityId = GenerateEntityId(entity.Text, entity.Type);

            var adjustedConfidence = entity.Confidence * 0.7;
            await _vectorStore.UpsertEntityAsync(entityId, entity.Text, entity.Type, adjustedConfidence);
            await _vectorStore.UpsertEntityMentionAsync(entityId, parentItem.Id, adjustedConfidence,
                $"[linked: {TruncateContext(linkedUrl, 100)}]");
        }
    }

    /// <summary>
    /// Find items similar to a query using HNSW vector search.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f)
    {
        return await _vectorStore.FindSimilarItemsAsync(queryEmbedding, topK, minSimilarity);
    }

    /// <summary>
    /// Display a mini knowledge graph in the console using Spectre.Console Tree.
    /// </summary>
    public async Task DisplayGraphAsync(int topN = 15, int? daysBack = null)
    {
        var (entityCount, relCount, mentionCount, itemCount) = await _vectorStore.GetStatsAsync();

        if (entityCount == 0)
        {
            AnsiConsole.MarkupLine("[grey]Knowledge graph empty. Entities are extracted on each run.[/]");
            return;
        }

        var entities = await _vectorStore.GetTopEntitiesAsync(topN, daysBack: daysBack);

        var tree = new Tree(
            $"[bold cyan]Knowledge Graph[/] [grey]({entityCount} entities, {relCount} edges, {mentionCount} mentions, {itemCount} items)[/]");

        // Group entities by type
        var byType = entities
            .OrderByDescending(e => e.FreshnessScore)
            .GroupBy(e => e.Type)
            .OrderByDescending(g => g.Sum(e => e.MentionCount));

        foreach (var typeGroup in byType)
        {
            var typeLabel = typeGroup.Key switch
            {
                "PER" => "[green]People[/]",
                "ORG" => "[blue]Organizations[/]",
                "LOC" => "[yellow]Locations[/]",
                _ => $"[grey]{typeGroup.Key}[/]"
            };

            var typeNode = tree.AddNode(typeLabel);

            foreach (var entity in typeGroup.OrderByDescending(e => e.FreshnessScore).Take(5))
            {
                var freshness = entity.FreshnessScore;
                var freshnessColor = freshness > 2 ? "green" : freshness > 0.5 ? "yellow" : "grey";
                var label =
                    $"{Markup.Escape(entity.Name)} [{freshnessColor}]({entity.MentionCount} mentions, {entity.ArticleCount} articles)[/]";
                var entityNode = typeNode.AddNode(label);

                // Show relationships (deduplicated by related entity name)
                var relationships = await _vectorStore.GetRelationshipsAsync(entity.Id);
                if (relationships.Count > 0)
                {
                    var relNode = entityNode.AddNode("[grey]Related:[/]");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rel in relationships.OrderByDescending(r => r.Weight).Take(6))
                    {
                        var otherName = rel.SourceId == entity.Id ? rel.TargetName : rel.SourceName;
                        if (!seen.Add(otherName)) continue; // skip duplicate names
                        relNode.AddNode($"[grey]{Markup.Escape(otherName)}[/] [dim](weight: {rel.Weight:F0})[/]");
                        if (seen.Count >= 4) break;
                    }
                }

                // Show article provenance (deduplicated by title)
                var articles = await _vectorStore.GetArticlesForEntityAsync(entity.Id);
                var uniqueArticles = articles
                    .GroupBy(a => a.title.ToLowerInvariant().Trim())
                    .Select(g => g.First())
                    .ToList();
                if (uniqueArticles.Count > 0)
                {
                    var artNode = entityNode.AddNode("[grey]Articles:[/]");
                    foreach (var (_, title, url, _) in uniqueArticles.Take(3))
                    {
                        var truncTitle = title.Length > 60 ? title[..57] + "..." : title;
                        artNode.AddNode($"[dim]{Markup.Escape(truncTitle)}[/]");
                    }
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(tree);
    }

    internal static string GenerateEntityId(string name, string type)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var input = $"{type}:{normalized}";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
        return $"{type.ToLowerInvariant()}_{hash}";
    }

    private static string TruncateContext(string text, int maxLen)
    {
        return text.Length > maxLen ? text[..maxLen] + "..." : text;
    }
}

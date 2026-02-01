using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Services;

/// <summary>
/// Consolidates per-item enrichment patterns shared across ScrollCommand, CrawlCommand, and PageCommand:
/// sentiment/topic scoring, keyword indexing, batch indexing, and entity graph persistence.
/// </summary>
public sealed class ItemProcessor
{
    private readonly float[] _positiveAnchor;
    private readonly float[] _negativeAnchor;
    private readonly Dictionary<string, float[]> _topicAnchors;
    private readonly StorageService _storage;
    private readonly IEntityGraphStore? _entityStore;

    /// <summary>
    /// Async factory: pre-computes sentiment and topic anchor embeddings once.
    /// </summary>
    public static async Task<ItemProcessor> CreateAsync(IEmbeddingService embedding, StorageService storage, IEntityGraphStore? entityStore = null, CancellationToken ct = default)
    {
        // Batch-embed all anchors in a single ONNX call (instead of 2 + N sequential calls)
        var topicKeys = RelevanceScorer.TopicAnchorTexts.Keys.ToList();
        var allTexts = new List<string>
        {
            RelevanceScorer.PositiveAnchorText,
            RelevanceScorer.NegativeAnchorText
        };
        allTexts.AddRange(topicKeys.Select(k => RelevanceScorer.TopicAnchorTexts[k]));

        var allEmbeddings = await embedding.EmbedBatchAsync(allTexts, ct);

        var positiveAnchor = allEmbeddings[0];
        var negativeAnchor = allEmbeddings[1];
        var topicAnchors = new Dictionary<string, float[]>();
        for (var i = 0; i < topicKeys.Count; i++)
            topicAnchors[topicKeys[i]] = allEmbeddings[i + 2];

        return new ItemProcessor(positiveAnchor, negativeAnchor, topicAnchors, storage, entityStore);
    }

    private ItemProcessor(float[] positiveAnchor, float[] negativeAnchor, Dictionary<string, float[]> topicAnchors, StorageService storage, IEntityGraphStore? entityStore = null)
    {
        _storage = storage;
        _entityStore = entityStore;
        _positiveAnchor = positiveAnchor;
        _negativeAnchor = negativeAnchor;
        _topicAnchors = topicAnchors;
    }

    /// <summary>
    /// Score sentiment and infer topic from pre-computed anchor embeddings.
    /// Thread-safe: reads only immutable pre-computed anchors + pure math.
    /// </summary>
    public void ScoreSentimentAndTopic(ContentItem item)
    {
        if (item.Embedding != null)
        {
            item.SentimentScore = RelevanceScorer.ComputeEmbeddingSentiment(
                item.Embedding, _positiveAnchor, _negativeAnchor);
            item.DetectedTopic = RelevanceScorer.InferTopic(item.Embedding, _topicAnchors);
        }
        else
        {
            item.DetectedTopic = InferTopicFromSource(item.Source);
        }
    }

    /// <summary>
    /// Per-item keyword profile extraction, FTS5 indexing, and corpus update.
    /// Used by CrawlCommand and PageCommand.
    /// </summary>
    public async Task IndexItemAsync(ContentItem item)
    {
        var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
        item.Keywords = profile.KeywordsText;

        await _storage.SaveItemAsync(item);

        var contentPreview = item.Content ?? "";
        await _storage.IndexDocumentFtsAsync(item.Id, item.Title, profile.KeywordsText, contentPreview);
        await _storage.UpdateKeywordCorpusAsync(profile.TopKeywords.Select(k => k.Keyword));
    }

    /// <summary>
    /// Batch keyword extraction + FTS5 indexing via single transaction.
    /// Used by ScrollCommand.
    /// </summary>
    public async Task IndexBatchAsync(List<ContentItem> items)
    {
        var batchEntries = items.Select(item =>
        {
            var kwProfile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
            if (string.IsNullOrEmpty(item.Keywords))
                item.Keywords = kwProfile.KeywordsText;
            return (item, kwProfile);
        }).ToList();

        await _storage.SaveAndIndexBatchAsync(batchEntries);
    }

    /// <summary>
    /// Deduplicate entities by name, persist to SQLite knowledge graph, and build co-occurrence edges.
    /// Used by ScrollCommand and CrawlCommand.
    /// </summary>
    public async Task PersistEntitiesAsync(ContentItem item, List<NerEntity> entities)
    {
        var deduped = entities
            .GroupBy(e => e.Text.ToLowerInvariant())
            .Select(g => g.OrderByDescending(e => e.Confidence).First())
            .ToList();

        var entityIds = new List<string>();
        foreach (var entity in deduped)
        {
            var entityId = KnowledgeGraphService.GenerateEntityId(entity.Text, entity.Type);
            entityIds.Add(entityId);
            await _entityStore!.UpsertEntityAsync(entityId, entity.Text, entity.Type, entity.Confidence);
            await _entityStore!.UpsertEntityMentionAsync(entityId, item.Id, entity.Confidence, item.Title);
        }

        // Build co-occurrence edges
        for (var i = 0; i < entityIds.Count; i++)
        {
            for (var j = i + 1; j < entityIds.Count; j++)
            {
                await _entityStore!.UpsertRelationshipAsync(entityIds[i], entityIds[j]);
            }
        }
    }

    /// <summary>
    /// Infer a basic topic from the source name when embeddings aren't available.
    /// </summary>
    public static string InferTopicFromSource(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "hn" => "technology",
            "reddit" => "technology",
            "bbc" or "guardian" or "cnn" or "reuters" => "world",
            "gnews" => "general",
            "so" => "technology",
            "ars" or "verge" => "technology",
            _ => "general"
        };
    }
}

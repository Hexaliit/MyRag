using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Mostlylucid.Summarizer.Core.Utilities;

namespace DoomSummarizer.Services;

/// <summary>
///     Single ingestion pipeline for all content paths (scroll, crawl, man, page).
///     Every IndexItemAsync / IndexBatchAsync call goes through the same steps:
///     1. Extract keywords (DocumentProfileService)
///     2. Save to SQLite
///     3. Index to FTS5
///     4. Update keyword corpus
///     5. Index to Lucene (if collection name provided)
/// </summary>
public sealed class ItemProcessor : IDisposable
{
    // ── Shared text-preparation utilities ──────────────────────────────
    // Used by all ingestion paths (crawl, scroll, man, page) so every item
    // is embedded, NER'd, and hashed with identical logic.

    public const int EmbeddingMaxChars = 1500;
    public const int NerMaxChars = 2000;
    private readonly IEntityGraphStore? _entityStore;
    private readonly LuceneSearchService? _lucene;
    private readonly float[] _negativeAnchor;
    private readonly float[] _positiveAnchor;
    private readonly StorageService _storage;
    private readonly Dictionary<string, float[]> _topicAnchors;
    private bool _luceneDirty;

    private ItemProcessor(
        float[] positiveAnchor,
        float[] negativeAnchor,
        Dictionary<string, float[]> topicAnchors,
        StorageService storage,
        IEntityGraphStore? entityStore,
        LuceneSearchService? lucene = null)
    {
        _storage = storage;
        _entityStore = entityStore;
        _positiveAnchor = positiveAnchor;
        _negativeAnchor = negativeAnchor;
        _topicAnchors = topicAnchors;
        _lucene = lucene;
    }

    public void Dispose()
    {
        CommitLucene();
        _lucene?.Dispose();
    }

    /// <summary>
    ///     Async factory: pre-computes sentiment and topic anchor embeddings once.
    ///     Pass collectionName to enable Lucene indexing on every IndexItemAsync/IndexBatchAsync call.
    /// </summary>
    public static async Task<ItemProcessor> CreateAsync(
        IEmbeddingService embedding,
        StorageService storage,
        IEntityGraphStore? entityStore = null,
        string? collectionName = null,
        CancellationToken ct = default)
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

        // Open Lucene writer if collection name provided
        LuceneSearchService? lucene = null;
        if (!string.IsNullOrEmpty(collectionName))
        {
            var lucenePath = Path.Combine(storage.DataPath, "lucene", collectionName);
            Directory.CreateDirectory(lucenePath);
            lucene = new LuceneSearchService(lucenePath);
            lucene.Open();
        }

        return new ItemProcessor(positiveAnchor, negativeAnchor, topicAnchors, storage, entityStore, lucene);
    }

    /// <summary>
    ///     Score sentiment and infer topic from pre-computed anchor embeddings.
    ///     Thread-safe: reads only immutable pre-computed anchors + pure math.
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
    ///     Full per-item indexing: keywords → SQLite → FTS5 → keyword corpus → Lucene.
    /// </summary>
    public async Task IndexItemAsync(ContentItem item)
    {
        var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
        item.Keywords = profile.KeywordsText;

        await _storage.SaveItemAsync(item);

        var contentPreview = item.Content ?? "";
        await _storage.IndexDocumentFtsAsync(item.Id, item.Title, profile.KeywordsText, contentPreview);
        await _storage.UpdateKeywordCorpusAsync(profile.TopKeywords.Select(k => k.Keyword));

        // Lucene — same step for every caller
        if (_lucene != null)
        {
            _lucene.IndexItem(item);
            _luceneDirty = true;
        }
    }

    /// <summary>
    ///     Batch indexing: keywords → SQLite + FTS5 → Lucene.
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

        // Lucene — same step for every caller
        if (_lucene != null)
        {
            _lucene.IndexItems(items);
            _luceneDirty = true;
        }
    }

    /// <summary>
    ///     Index items into Lucene only (no SQLite save). Used for URL-cached items
    ///     that are already in storage but need to appear in the current collection's Lucene index.
    ///     Idempotent: Lucene UpdateDocument handles duplicates by ID.
    /// </summary>
    public void EnsureInLucene(IEnumerable<ContentItem> items)
    {
        if (_lucene == null) return;
        _lucene.IndexItems(items);
        _luceneDirty = true;
    }

    /// <summary>
    ///     Commit pending Lucene writes to disk. Call after a batch of IndexItemAsync/IndexBatchAsync calls.
    ///     Also called automatically on Dispose.
    /// </summary>
    public void CommitLucene()
    {
        if (_lucene != null && _luceneDirty)
        {
            _lucene.Commit();
            _luceneDirty = false;
        }
    }

    /// <summary>
    ///     Deduplicate entities by name, persist to SQLite knowledge graph, and build co-occurrence edges.
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
        for (var j = i + 1; j < entityIds.Count; j++)
            await _entityStore!.UpsertRelationshipAsync(entityIds[i], entityIds[j]);
    }

    /// <summary>
    ///     Infer a basic topic from the source name when embeddings aren't available.
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

    /// <summary>
    ///     Format text for embedding: "Title: Content", truncated to <see cref="EmbeddingMaxChars" />.
    ///     Colon separator matches TreeRAG hierarchical context format.
    /// </summary>
    public static string PrepareEmbeddingText(string? title, string? content)
    {
        var text = $"{title}: {content ?? ""}".Trim();
        return text.Length > EmbeddingMaxChars ? text[..EmbeddingMaxChars] : text;
    }

    /// <summary>
    ///     Format text for NER entity extraction: "Title Content[..maxChars]".
    ///     Space separator (not colon) so NER doesn't confuse punctuation with entities.
    /// </summary>
    public static string PrepareNerText(string? title, string? content, int maxChars = NerMaxChars)
    {
        var truncatedContent = content != null && content.Length > maxChars
            ? content[..maxChars]
            : content ?? "";
        return $"{title} {truncatedContent}";
    }

    /// <summary>
    ///     Compute content hash for cache comparison (ETag fallback).
    ///     Uses XxHash64 via shared ContentHasher — fast, non-cryptographic.
    /// </summary>
    public static string ComputeContentHash(string content)
    {
        return ContentHasher.ComputeHash(content);
    }
}
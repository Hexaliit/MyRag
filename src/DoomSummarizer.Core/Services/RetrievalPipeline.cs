using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
/// Unified retrieval and scoring pipeline. All DoomSummarizer retrieval flows
/// through this class — KB search (AskCommand), local scroll, web scroll, and MCP tools.
///
/// Two entry points:
/// - SearchAsync: Full pipeline (candidate retrieval + scoring) for KB/local queries
/// - ScoreItemsAsync: Scoring-only for pre-fetched items (web-mode, MCP tools)
///
/// Both share the same 5-signal RRF scoring (+ optional Lucene FTS signal)
/// with PRF centroid refinement, outlier penalty, and embedding dedup.
/// BM25 keyword matching is handled exclusively by Lucene at the retrieval layer.
/// </summary>
public sealed class RetrievalPipeline
{
    private readonly IEmbeddingService _embedding;
    private readonly StorageService _storage;
    private readonly IEntityGraphStore? _entityStore;

    public RetrievalPipeline(
        IEmbeddingService embedding,
        StorageService storage,
        IEntityGraphStore? entityStore = null)
    {
        _embedding = embedding;
        _storage = storage;
        _entityStore = entityStore;
    }

    /// <summary>
    /// Execute the full retrieval pipeline: Lucene FTS + embedding HNSW + entity profile HNSW,
    /// fused via 6-signal RRF with PRF centroid refinement and outlier penalty.
    /// </summary>
    public async Task<RetrievalResult> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return RetrievalResult.Empty;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Detect query type for adaptive RRF weights
        var queryType = options.QueryType ?? QueryTypeDetector.Detect(query);

        // Embed the query
        var queryEmbedding = await _embedding.EmbedAsync(query, ct);

        // --- Candidate retrieval (3 layers) ---
        var luceneResults = new List<LuceneSearchResult>();
        var embeddingResults = new List<StoredItem>();
        var entityProfileIds = new HashSet<string>();

        // Layer 1: Lucene FTS (fuzzy, phrase, field-boosted)
        if (options.UseLucene)
        {
            try
            {
                var collectionName = options.CollectionName ?? "default";
                var luceneIndexPath = Path.Combine(_storage.DataPath, "lucene", collectionName);
                using var lucene = new LuceneSearchService(luceneIndexPath);
                lucene.Open();

                // Ensure items are indexed in Lucene (incremental update)
                var allStoredItems = await _storage.GetRecentItemsAsync(days: 365, source: options.SourceFilter);
                var itemsToIndex = allStoredItems
                    .Where(s => !lucene.ContainsDocument(s.Id))
                    .Select(s => s.ToContentItem())
                    .ToList();

                if (itemsToIndex.Count > 0)
                {
                    lucene.IndexItems(itemsToIndex);
                    lucene.Commit();
                }

                // Generate Lucene query (deterministic, no LLM needed)
                var luceneQuery = LuceneQueryGenerator.BuildSimpleQuery(query);

                // Search Lucene
                luceneResults = lucene.Search(luceneQuery, options.SourceFilter, limit: options.TopK * 3);
            }
            catch
            {
                // Lucene search is best-effort; fall through to embedding search
            }
        }

        // Layer 2: Embedding HNSW semantic search
        embeddingResults = await _storage.FindSimilarAsync(
            queryEmbedding, limit: options.TopK * 2, threshold: 0.20, source: options.SourceFilter);

        // Layer 3: Entity profile HNSW search (when entity profiles exist)
        if (options.UseEntityProfiles && _entityStore != null && options.QueryEntities?.Count >= 2)
        {
            try
            {
                var hasProfiles = await _entityStore.HasEntityProfilesAsync();
                if (hasProfiles)
                {
                    var entityProfileService = new EntityProfileService(_embedding);
                    var entityDocCounts = await _entityStore.GetEntityDocCountsAsync();
                    var totalDocs = await _entityStore.GetTotalDocsWithEntitiesAsync();

                    var queryEntities = options.QueryEntities
                        .Select(e => (name: e, type: EntityProfileService.InferEntityType(e), confidence: 0.8f))
                        .ToList();

                    var queryEntityProfile = await entityProfileService.ComputeQueryProfileAsync(
                        queryEntities, entityDocCounts, totalDocs);

                    if (queryEntityProfile.Length > 0)
                    {
                        var entityResults = await _entityStore.FindRelatedByEntityProfileAsync(
                            queryEntityProfile, topK: options.TopK, minSimilarity: 0.25f);

                        foreach (var (itemId, _, _) in entityResults)
                            entityProfileIds.Add(itemId);
                    }
                }
            }
            catch
            {
                // Entity profile search is best-effort
            }
        }

        // --- Fuse candidate IDs ---
        var luceneIds = luceneResults.Select(r => r.Id).ToHashSet();
        var embeddingIds = embeddingResults.Select(r => r.Id).ToHashSet();
        var allCandidateIds = new HashSet<string>(luceneIds);
        allCandidateIds.UnionWith(embeddingIds);
        allCandidateIds.UnionWith(entityProfileIds);

        if (allCandidateIds.Count == 0)
        {
            // Fallback: load recent items directly
            var fallbackItems = options.SourceFilter != null
                ? await _storage.GetRecentItemsAsync(days: 365, source: options.SourceFilter)
                : await _storage.GetRecentItemsAsync(days: 30);

            var fallbackContent = fallbackItems
                .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                .Select(s => s.ToContentItem())
                .Take(options.TopK)
                .ToList();

            return new RetrievalResult(fallbackContent, queryType, sw.Elapsed);
        }

        // Load full items
        var items = await _storage.LoadItemsByIdsAsync(allCandidateIds.ToList());

        // Capture Lucene FTS scores for RRF fusion — these provide keyword precision
        // that complements the scorer's embedding-based semantic similarity signal.
        var luceneScoreLookup = luceneResults.Count > 0
            ? luceneResults.ToDictionary(r => r.Id, r => (double)r.Score)
            : null;

        // Filter out items without any content
        items = items
            .Where(i => !string.IsNullOrEmpty(i.Summary) || !string.IsNullOrEmpty(i.Title))
            .ToList();

        if (items.Count == 0)
            return RetrievalResult.Empty;

        // --- Score through the unified pipeline ---
        var scoringResult = await ScoreItemsAsync(items, new ScoringOptions
        {
            Query = query,
            QueryEmbedding = queryEmbedding,
            IsKnowledgeBase = options.IsKnowledgeBase,
            QueryType = options.QueryType,
            TextRelevanceScores = luceneScoreLookup,
            UseOutlierPenalty = true,
            UseEmbeddingDedup = options.UseEmbeddingDedup,
        }, ct);

        // Apply minimum relevance filter and take topK
        var finalItems = scoringResult.Items
            .Where(i => i.RelevanceScore > options.MinRelevance)
            .Take(options.TopK)
            .ToList();

        sw.Stop();
        return new RetrievalResult(finalItems, scoringResult.QueryType, sw.Elapsed);
    }

    /// <summary>
    /// Score pre-fetched items through the 5-signal RRF pipeline (+ optional Lucene FTS signal).
    /// This is the single scoring path for ALL DoomSummarizer retrieval:
    /// - SearchAsync calls this after candidate retrieval (KB/local mode, with Lucene FTS scores)
    /// - ScrollCommand calls this after web fetching (web mode, no Lucene scores)
    /// - DoomScrollerTools calls this for MCP tool queries
    ///
    /// Pipeline: keyword profiles → embeddings → quality anchors →
    /// Phase 1 ScoreFast → PRF centroid → Phase 2 ScoreFull →
    /// outlier penalty → embedding dedup.
    /// </summary>
    public async Task<ScoringResult> ScoreItemsAsync(
        List<ContentItem> items,
        ScoringOptions options,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
            return new ScoringResult([], options.QueryType ?? QueryType.General, null);

        var queryType = options.QueryType ?? QueryTypeDetector.Detect(options.Query);

        // Create scorer: KB mode zeroes authority/freshness (no discrimination in crawled collections)
        var scorer = options.IsKnowledgeBase
            ? RelevanceScorer.ForKnowledgeBase(queryType)
            : RelevanceScorer.ForQueryType(queryType);

        // Compute keyword profiles for items that don't have them
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Keywords))
            {
                var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
                item.Keywords = profile.KeywordsText;
            }
        }

        // Compute embeddings for items that need them
        var itemsNeedingEmbedding = items.Where(i => i.Embedding == null).ToList();
        if (itemsNeedingEmbedding.Count > 0)
        {
            var textsToEmbed = itemsNeedingEmbedding
                .Select(item => $"{item.Title} {item.Content ?? ""}".Trim())
                .ToList();
            var embeddings = await _embedding.EmbedBatchAsync(textsToEmbed, ct);
            for (var i = 0; i < itemsNeedingEmbedding.Count; i++)
                itemsNeedingEmbedding[i].Embedding = embeddings[i];
        }

        // Quality anchors: detect clickbait vs substantive content
        var highQualityAnchor = await _embedding.EmbedAsync(RelevanceScorer.HighQualityAnchorText, ct);
        var lowQualityAnchor = await _embedding.EmbedAsync(RelevanceScorer.LowQualityAnchorText, ct);
        scorer = scorer.WithQualityAnchors(highQualityAnchor, lowQualityAnchor);

        // Compute vibe embedding if specified
        float[]? vibeEmbedding = null;
        if (!string.IsNullOrEmpty(options.VibeText))
            vibeEmbedding = await _embedding.EmbedAsync(options.VibeText, ct);

        // Phase 1: Fast discard (freshness + authority + semantic + optional Lucene FTS)
        if (items.Count > 5)
        {
            items = scorer.ScoreFast(items, options.Query, discardRatio: 0.25,
                queryEmbedding: options.QueryEmbedding,
                textRelevanceScores: options.TextRelevanceScores);
        }

        // PRF centroid refinement: blend query embedding with top-5 centroid
        float[]? refinedQueryEmbedding = options.QueryEmbedding;
        if (items.Count >= 5)
        {
            refinedQueryEmbedding = RelevanceScorer.ComputePRFCentroid(items, options.QueryEmbedding);
        }

        // Phase 2: Full RRF with all signals (+ optional Lucene FTS)
        // Pass the original (non-PRF) query embedding as gateEmbedding so the hard gate
        // filters on topical alignment with the actual query, not the PRF centroid.
        items = scorer.ScoreFull(items, options.Query, refinedQueryEmbedding,
            vibeEmbedding: vibeEmbedding,
            gateEmbedding: options.QueryEmbedding,
            textRelevanceScores: options.TextRelevanceScores);

        // Outlier penalty (skip for Roundup queries where diverse topics are expected)
        // Use the ORIGINAL query embedding (not PRF-refined) to detect outliers relative
        // to the actual query, preventing PRF centroid drift from masking off-topic items.
        if (options.UseOutlierPenalty && queryType != QueryType.Roundup && !string.IsNullOrWhiteSpace(options.Query))
        {
            var penalties = RelevanceScorer.ComputeQueryTermCoverage(
                items, [], new Dictionary<string, double>(), queryEmbedding: options.QueryEmbedding);
            RelevanceScorer.ApplyOutlierPenalties(items, penalties);
            items = items.OrderByDescending(i => i.RelevanceScore).ToList();
        }

        // Embedding-based dedup (cosine threshold 0.90)
        if (options.UseEmbeddingDedup)
        {
            items = DeduplicateByEmbedding(items, threshold: 0.90f);
        }

        return new ScoringResult(items, queryType, refinedQueryEmbedding);
    }

    /// <summary>
    /// Deduplicate items by embedding cosine similarity. Keeps the highest-scored item
    /// when similar content is found (threshold 0.90 = near-identical meaning).
    /// Complements URL/title dedup for KB queries where same content appears at different URLs.
    /// </summary>
    private static List<ContentItem> DeduplicateByEmbedding(List<ContentItem> items, float threshold = 0.90f)
    {
        if (items.Count <= 1) return items;

        var result = new List<ContentItem>();
        var seen = new List<float[]>(); // Embeddings of kept items

        foreach (var item in items.OrderByDescending(i => i.RelevanceScore))
        {
            if (item.Embedding == null)
            {
                result.Add(item); // Can't dedup without embedding
                continue;
            }

            var isDuplicate = false;
            foreach (var seenEmb in seen)
            {
                var sim = VectorMath.CosineSimilarity(item.Embedding, seenEmb);
                if (sim >= threshold)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                result.Add(item);
                seen.Add(item.Embedding);
            }
        }

        return result;
    }
}

/// <summary>
/// Options for configuring the retrieval pipeline.
/// </summary>
public record RetrievalOptions
{
    /// <summary>Source filter (e.g., "crawl:mysite").</summary>
    public string? SourceFilter { get; init; }

    /// <summary>Collection name for Lucene index directory.</summary>
    public string? CollectionName { get; init; }

    /// <summary>Number of results to return.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Minimum relevance score threshold.</summary>
    public float MinRelevance { get; init; } = 0.15f;

    /// <summary>Whether to use entity profile HNSW search.</summary>
    public bool UseEntityProfiles { get; init; } = true;

    /// <summary>Whether to use Lucene FTS search.</summary>
    public bool UseLucene { get; init; } = true;

    /// <summary>Whether to use embedding-based deduplication.</summary>
    public bool UseEmbeddingDedup { get; init; } = true;

    /// <summary>Override query type detection.</summary>
    public QueryType? QueryType { get; init; }

    /// <summary>
    /// Whether source items are KB (crawl/page) where authority/freshness provide zero discrimination.
    /// When true, uses ForKnowledgeBase() scorer weights.
    /// </summary>
    public bool IsKnowledgeBase { get; init; } = true;

    /// <summary>
    /// Entity names extracted from the query (e.g., by Sentinel or NER).
    /// When at least 2 entities are present, entity profile HNSW search is enabled.
    /// </summary>
    public List<string>? QueryEntities { get; init; }
}

/// <summary>
/// Result from the retrieval pipeline, including scored items, detected query type, and timing.
/// </summary>
public record RetrievalResult(
    List<ContentItem> Items,
    QueryType QueryType,
    TimeSpan Elapsed)
{
    public static readonly RetrievalResult Empty = new([], QueryType.General, TimeSpan.Zero);
}

/// <summary>
/// Options for the scoring-only pipeline (ScoreItemsAsync).
/// Used when items are already fetched (web-mode, MCP tools) and just need scoring.
/// </summary>
public record ScoringOptions
{
    /// <summary>The original query text (used for outlier detection, query type).</summary>
    public required string Query { get; init; }

    /// <summary>Pre-computed query embedding (used for semantic similarity, PRF centroid).</summary>
    public required float[] QueryEmbedding { get; init; }

    /// <summary>
    /// Vibe text to embed for vibe alignment scoring (e.g., "analytical, serious, data-driven").
    /// Null = no vibe signal (neutral).
    /// </summary>
    public string? VibeText { get; init; }

    /// <summary>
    /// Whether source items are KB (crawl/page) where authority/freshness provide zero discrimination.
    /// When true, uses ForKnowledgeBase() scorer weights.
    /// </summary>
    public bool IsKnowledgeBase { get; init; }

    /// <summary>Override query type detection.</summary>
    public QueryType? QueryType { get; init; }

    /// <summary>
    /// Pre-computed text relevance scores (e.g., Lucene FTS scores).
    /// When provided, included as an RRF signal for keyword precision.
    /// KB mode passes Lucene scores from retrieval; web mode may be null.
    /// </summary>
    public Dictionary<string, double>? TextRelevanceScores { get; init; }

    /// <summary>Whether to apply outlier penalty for off-topic items.</summary>
    public bool UseOutlierPenalty { get; init; } = true;

    /// <summary>Whether to use embedding-based deduplication.</summary>
    public bool UseEmbeddingDedup { get; init; } = true;
}

/// <summary>
/// Result from the scoring pipeline (ScoreItemsAsync).
/// Contains scored items, detected query type, and the PRF-refined embedding.
/// </summary>
public record ScoringResult(
    List<ContentItem> Items,
    QueryType QueryType,
    float[]? RefinedQueryEmbedding);

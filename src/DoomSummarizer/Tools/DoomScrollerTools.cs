using System.ComponentModel;
using System.Text.Json;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using ModelContextProtocol.Server;

namespace DoomSummarizer.Tools;

/// <summary>
/// MCP tools exposing DoomSummarizer's knowledge base, entity graph, and search pipeline
/// to AI agents via the Model Context Protocol.
///
/// Start with: doomsummarizer --mcp
///
/// Tool categories:
///   Search:      search_kb, keyword_search, semantic_search
///   Content:     get_item_content, extract_keywords, compare_items
///   Ingestion:   ingest_url
///   Collections: list_collections, get_collection_items
///   Entities:    list_entities, get_entity_details, find_related_by_entities, get_entity_network
///   Analytics:   get_kb_stats, get_trends
///
/// All tools return JSON. Services are lazily initialized on first call.
/// </summary>
[McpServerToolType]
public static class DoomScrollerTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Shared HttpClient (static to avoid socket exhaustion)
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "MostlyLucid-DoomSummarizer/1.0 (MCP)" } }
    };

    // Lazy-initialized shared services
    private static StorageService? _storage;
    private static EmbeddingService? _embedding;
    private static DoomConfig? _config;
    private static bool _servicesInitialized;
    private static readonly SemaphoreSlim InitLock = new(1, 1);

    private static async Task EnsureServicesAsync()
    {
        if (_servicesInitialized) return;

        await InitLock.WaitAsync();
        try
        {
            if (_servicesInitialized) return;

            _config = await ConfigService.LoadAsync();
            var dbPath = ConfigService.GetDbPath(_config);

            _storage = new StorageService(dbPath);
            await _storage.InitializeAsync();

            _embedding = new EmbeddingService();
            if (_embedding.IsSetup)
                _embedding.Initialize();

            _servicesInitialized = true;
        }
        finally
        {
            InitLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  SEARCH
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "search_kb")]
    [Description(
        "Search the knowledge base using the full relevance pipeline: " +
        "FTS5 pre-filter → BM25F scoring with global IDF → embedding similarity → " +
        "RRF (Reciprocal Rank Fusion) combining 4+ signals. " +
        "Returns ranked results with IDs (use with get_item_content), titles, URLs, " +
        "summaries, keywords, and multi-signal relevance scores. " +
        "Filter by source (e.g., 'crawl:docs', 'hn', 'bbc').")]
    public static async Task<string> SearchKbAsync(
        [Description("Search query (e.g., 'history of LLMs', 'AI security vulnerabilities')")]
        string query,
        [Description("Optional source/collection filter (e.g., 'crawl:docs'). Omit to search all.")]
        string? source = null,
        [Description("Maximum results to return (default: 10, max: 50)")]
        int limit = 10)
    {
        try
        {
            await EnsureServicesAsync();
            limit = Math.Clamp(limit, 1, 50);

            // Layer 1: FTS5 pre-filter (deterministic SQL)
            var candidateIds = await _storage!.FtsPreFilterAsync(query, source: source, limit: limit * 3);

            List<ContentItem> items;
            if (candidateIds.Count > 0)
            {
                items = await _storage.LoadItemsByIdsAsync(candidateIds);
            }
            else if (_embedding is { IsSetup: true })
            {
                // Fallback: embedding search when FTS5 misses
                var queryEmbed = _embedding.Embed(query);
                var similar = await _storage.FindSimilarAsync(queryEmbed, limit: limit * 2, threshold: 0.20, source: source);
                items = similar.Select(s => s.ToContentItem()).ToList();
            }
            else
            {
                return Json(new { success = true, query, results = Array.Empty<object>(),
                    message = "No results. FTS5 returned no matches and embeddings unavailable." });
            }

            if (items.Count == 0)
                return Json(new { success = true, query, results = Array.Empty<object>() });

            // Ensure keyword profiles
            foreach (var item in items.Where(i => string.IsNullOrEmpty(i.Keywords)))
            {
                var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
                item.Keywords = profile.KeywordsText;
            }

            // Layer 2: Full RRF pipeline with global IDF
            var globalCorpus = await _storage.GetKeywordCorpusAsync();
            var globalCorpusSize = await _storage.GetKeywordCorpusSizeAsync();

            float[]? queryEmbedding = _embedding is { IsSetup: true } ? _embedding.Embed(query) : null;
            var bm25Query = query;

            var scorer = new RelevanceScorer();
            items = scorer.ScoreFast(items, bm25Query, discardRatio: 0.25,
                queryEmbedding: queryEmbedding,
                globalCorpus: globalCorpus, globalCorpusSize: globalCorpusSize);

            // PRF refinement (only with enough results to avoid drift)
            float[]? refinedEmbedding = queryEmbedding;
            if (items.Count >= 5 && queryEmbedding != null)
                refinedEmbedding = RelevanceScorer.ComputePRFCentroid(items, queryEmbedding);

            // ScoreFull requires a non-null embedding; skip if embeddings unavailable
            var finalEmbedding = refinedEmbedding ?? queryEmbedding;
            if (finalEmbedding != null)
            {
                items = scorer.ScoreFull(items, bm25Query,
                    queryEmbedding: finalEmbedding,
                    globalCorpus: globalCorpus, globalCorpusSize: globalCorpusSize);
            }

            var rankedItems = items.Take(limit).ToList();

            var results = rankedItems.Select((item, idx) =>
            {
                var cosineSim = queryEmbedding != null && item.Embedding != null
                    ? (double?)Math.Round(EmbeddingService.CosineSimilarity(item.Embedding, queryEmbedding), 3)
                    : null;

                return new
                {
                    rank = idx + 1,
                    id = item.Id,
                    title = item.Title,
                    url = item.Url,
                    source = item.Source,
                    summary = Truncate(item.Summary ?? item.Content, 300),
                    keywords = item.Keywords,
                    relevance_score = Math.Round(item.RelevanceScore, 4),
                    cosine_similarity = cosineSim,
                    created_at = item.CreatedAt.ToString("yyyy-MM-dd"),
                    topic = item.DetectedTopic,
                    sentiment = Math.Round(item.SentimentScore, 2)
                };
            }).ToList();

            return Json(new
            {
                success = true,
                query, source_filter = source,
                pipeline = "FTS5 → ScoreFast (BM25F+Freshness+Authority) → PRF → ScoreFull (+QuerySim+Vibe) → RRF",
                total_candidates = candidateIds.Count > 0 ? candidateIds.Count : items.Count,
                result_count = results.Count,
                global_corpus = new { terms = globalCorpus.Count, docs = globalCorpusSize },
                results
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "keyword_search")]
    [Description(
        "Fast keyword search using SQLite FTS5 with Porter stemming. " +
        "Returns item IDs, titles, and URLs. Lighter than search_kb — " +
        "use when you just need to check if content exists on a topic.")]
    public static async Task<string> KeywordSearchAsync(
        [Description("Keywords to search (stemming applied: 'running' matches 'run')")]
        string keywords,
        [Description("Optional source filter")]
        string? source = null,
        [Description("Max results (default: 20)")]
        int limit = 20)
    {
        try
        {
            await EnsureServicesAsync();

            var ids = await _storage!.FtsPreFilterAsync(keywords, source: source, limit: limit);
            if (ids.Count == 0)
                return Json(new { success = true, keywords, results = Array.Empty<object>() });

            var storedItems = await _storage.GetItemsByIdsAsync(ids);
            var results = storedItems.Select(s => new
            {
                id = s.Id, title = s.Title, url = s.Url,
                source = s.Source, created_at = s.CreatedAt.ToString("yyyy-MM-dd")
            }).ToList();

            return Json(new { success = true, keywords, source_filter = source,
                result_count = results.Count, results });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "semantic_search")]
    [Description(
        "Search by meaning using embedding cosine similarity. " +
        "Finds semantically similar content even when keywords differ. " +
        "Requires the ONNX embedding model (all-MiniLM-L6-v2). " +
        "Use when keyword_search fails but you know the topic.")]
    public static async Task<string> SemanticSearchAsync(
        [Description("Natural language query to find semantically similar content")]
        string query,
        [Description("Minimum cosine similarity threshold 0.0-1.0 (default: 0.25)")]
        double threshold = 0.25,
        [Description("Optional source filter")]
        string? source = null,
        [Description("Max results (default: 10)")]
        int limit = 10)
    {
        try
        {
            await EnsureServicesAsync();

            if (_embedding is not { IsSetup: true })
                return Json(new { success = false, error = "Embedding model not available. Run 'doomsummarizer setup' first." });

            var queryEmbed = _embedding.Embed(query);
            var similar = await _storage!.FindSimilarAsync(queryEmbed, limit: limit,
                threshold: threshold, source: source);

            var results = similar.Select((s, idx) =>
            {
                var cosine = s.Embedding != null
                    ? EmbeddingService.CosineSimilarity(EmbeddingService.FromBytes(s.Embedding), queryEmbed)
                    : 0f;
                return new
                {
                    rank = idx + 1, id = s.Id, title = s.Title, url = s.Url,
                    source = s.Source, cosine_similarity = Math.Round(cosine, 4),
                    summary = Truncate(s.Summary ?? s.Content, 200),
                    created_at = s.CreatedAt.ToString("yyyy-MM-dd")
                };
            }).ToList();

            return Json(new { success = true, query, threshold,
                source_filter = source, result_count = results.Count, results });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  CONTENT ACCESS & ANALYSIS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "get_item_content")]
    [Description(
        "Get the full content of a knowledge base item by ID. " +
        "Returns complete text, summary, keywords, metadata, and structural analysis. " +
        "Use after search_kb or keyword_search to read full articles.")]
    public static async Task<string> GetItemContentAsync(
        [Description("Item ID (from search results)")]
        string itemId,
        [Description("Maximum content length to return in chars (default: 10000, 0 = unlimited)")]
        int maxLength = 10000)
    {
        try
        {
            await EnsureServicesAsync();

            var items = await _storage!.GetItemsByIdsAsync([itemId]);
            if (items.Count == 0)
                return Json(new { success = false, error = $"Item '{itemId}' not found" });

            var item = items[0];
            var content = item.Content ?? item.Summary ?? "";
            var fullLength = content.Length;
            if (maxLength > 0 && content.Length > maxLength)
                content = content[..maxLength] + $"\n\n[Truncated at {maxLength}/{fullLength} chars. Set maxLength=0 for full content.]";

            // Compute keyword profile if missing
            string? keywordsText = item.Keywords;
            List<object>? topKeywords = null;
            if (string.IsNullOrEmpty(keywordsText) && !string.IsNullOrEmpty(item.Content))
            {
                var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content);
                keywordsText = profile.KeywordsText;
                topKeywords = profile.TopKeywords.Select(k => (object)new
                {
                    keyword = k.Keyword, weight = Math.Round(k.Weight, 1)
                }).ToList();
            }

            // Get entities for this item
            var entityMap = await _storage.GetEntitiesForItemsAsync([itemId]);
            var entities = entityMap.GetValueOrDefault(itemId)?
                .Select(e => new { name = e.name, type = e.type, confidence = Math.Round(e.confidence, 2) })
                .ToList();

            return Json(new
            {
                success = true,
                id = item.Id, title = item.Title, url = item.Url, source = item.Source,
                content,
                content_length = fullLength,
                summary = item.Summary,
                keywords = keywordsText,
                top_keywords = topKeywords,
                topic = item.DetectedTopic,
                sentiment = Math.Round(item.SentimentScore, 2),
                created_at = item.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                has_embedding = item.Embedding != null,
                entities
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "extract_keywords")]
    [Description(
        "Extract structurally-weighted keywords from arbitrary text. " +
        "Uses heading/intro/body zone weighting (no LLM). " +
        "Returns top unigrams and bigrams with weights. " +
        "Useful for profiling documents before searching or comparing.")]
    public static Task<string> ExtractKeywordsAsync(
        [Description("Title of the document")]
        string title,
        [Description("Full text content (markdown or plain text)")]
        string content)
    {
        try
        {
            var profile = DocumentProfileService.ExtractProfile(title, content);

            var unigrams = profile.TopKeywords.Select(k => new
            {
                keyword = k.Keyword, weight = Math.Round(k.Weight, 1)
            }).ToList();

            var bigrams = profile.TopBigrams.Select(b => new
            {
                phrase = b.ngram, count = b.count
            }).ToList();

            return Task.FromResult(Json(new
            {
                success = true,
                title, content_length = content.Length,
                keywords_text = profile.KeywordsText,
                unigrams, bigrams,
                weighting = "title:4x, headings:3x, intro:2x, body:1x"
            }));
        }
        catch (Exception ex) { return Task.FromResult(Json(new { success = false, error = ex.Message })); }
    }

    [McpServerTool(Name = "compare_items")]
    [Description(
        "Compare two knowledge base items by cosine similarity of their embeddings. " +
        "Returns similarity score and keyword overlap. " +
        "Useful for deduplication or finding how related two articles are.")]
    public static async Task<string> CompareItemsAsync(
        [Description("First item ID")]
        string itemId1,
        [Description("Second item ID")]
        string itemId2)
    {
        try
        {
            await EnsureServicesAsync();

            if (itemId1 == itemId2)
                return Json(new { success = false, error = "Both IDs are the same. Provide two different item IDs to compare." });

            var items = await _storage!.GetItemsByIdsAsync([itemId1, itemId2]);
            if (items.Count < 2)
                return Json(new { success = false, error = "One or both items not found" });

            var item1 = items.First(i => i.Id == itemId1);
            var item2 = items.First(i => i.Id == itemId2);

            double? cosineSim = null;
            if (item1.Embedding != null && item2.Embedding != null)
            {
                var e1 = EmbeddingService.FromBytes(item1.Embedding);
                var e2 = EmbeddingService.FromBytes(item2.Embedding);
                cosineSim = Math.Round(EmbeddingService.CosineSimilarity(e1, e2), 4);
            }

            // Keyword overlap
            var kw1 = RelevanceScorer.Tokenize(item1.Keywords ?? item1.Title);
            var kw2 = RelevanceScorer.Tokenize(item2.Keywords ?? item2.Title);
            var shared = kw1.Intersect(kw2, StringComparer.OrdinalIgnoreCase).ToList();
            var jaccard = kw1.Count + kw2.Count > 0
                ? Math.Round((double)shared.Count / kw1.Union(kw2, StringComparer.OrdinalIgnoreCase).Count(), 3)
                : 0.0;

            return Json(new
            {
                success = true,
                item1 = new { id = item1.Id, title = item1.Title, source = item1.Source },
                item2 = new { id = item2.Id, title = item2.Title, source = item2.Source },
                cosine_similarity = cosineSim,
                keyword_overlap = new
                {
                    shared_keywords = shared,
                    jaccard_index = jaccard,
                    item1_keywords = kw1.Count,
                    item2_keywords = kw2.Count
                },
                interpretation = cosineSim switch
                {
                    > 0.9 => "Nearly identical content",
                    > 0.7 => "Strongly related / same topic",
                    > 0.5 => "Moderately related",
                    > 0.3 => "Loosely related",
                    _ => cosineSim.HasValue ? "Different topics" : "Cannot compare (missing embeddings)"
                }
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  INGESTION
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "ingest_url")]
    [Description(
        "Fetch a URL, extract readable content, compute embeddings, keyword profile, " +
        "sentiment, topic, and store in the knowledge base. " +
        "Returns the new item ID for use with other tools. " +
        "Uses SmartReader (Mozilla Readability) for content extraction.")]
    public static async Task<string> IngestUrlAsync(
        [Description("URL to fetch and index")]
        string url,
        [Description("Source label for the collection (default: 'mcp')")]
        string source = "mcp")
    {
        try
        {
            await EnsureServicesAsync();

            var extractor = new ContentExtractor(SharedHttp);
            var extracted = await extractor.ExtractAsync(url);

            if (extracted == null || string.IsNullOrWhiteSpace(extracted.Content))
                return Json(new { success = false, error = "Could not extract readable content from URL" });

            var item = new ContentItem
            {
                Id = $"mcp-{Guid.NewGuid():N}",
                Source = source,
                Title = extracted.Title,
                Url = url,
                Content = extracted.BestContent,
                Author = extracted.Author,
                IsEnriched = true,
                CreatedAt = DateTimeOffset.UtcNow,
                FetchedAt = DateTimeOffset.UtcNow
            };

            // Embedding
            if (_embedding is { IsSetup: true })
            {
                var textToEmbed = $"{item.Title} {item.Content}".Trim();
                item.Embedding = _embedding.Embed(textToEmbed);

                // Sentiment + topic via embedding anchors
                var positiveAnchor = _embedding.Embed(RelevanceScorer.PositiveAnchorText);
                var negativeAnchor = _embedding.Embed(RelevanceScorer.NegativeAnchorText);
                item.SentimentScore = RelevanceScorer.ComputeEmbeddingSentiment(
                    item.Embedding, positiveAnchor, negativeAnchor);

                var topicAnchors = RelevanceScorer.TopicAnchorTexts.ToDictionary(
                    kv => kv.Key, kv => _embedding.Embed(kv.Value));
                item.DetectedTopic = RelevanceScorer.InferTopic(item.Embedding, topicAnchors);
            }

            // Keyword profile
            var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
            item.Keywords = profile.KeywordsText;

            // Store
            await _storage!.SaveItemAsync(item);

            // FTS5 index
            await _storage.IndexDocumentFtsAsync(item.Id, item.Title, profile.KeywordsText,
                (item.Content ?? "").Length > 2000 ? item.Content![..2000] : item.Content ?? "");
            await _storage.UpdateKeywordCorpusAsync(profile.TopKeywords.Select(k => k.Keyword));

            return Json(new
            {
                success = true,
                id = item.Id,
                title = item.Title,
                url,
                source,
                content_length = extracted.Content.Length,
                read_time_minutes = extracted.ReadTimeMinutes,
                author = extracted.Author,
                topic = item.DetectedTopic,
                sentiment = Math.Round(item.SentimentScore, 2),
                has_embedding = item.Embedding != null,
                keywords = profile.KeywordsText,
                top_keywords = profile.TopKeywords.Take(10).Select(k => k.Keyword).ToList()
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  COLLECTIONS & ITEMS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "list_collections")]
    [Description(
        "List all knowledge base collections with item counts, embedding coverage, " +
        "and date ranges. Use source names with search_kb or get_collection_items.")]
    public static async Task<string> ListCollectionsAsync()
    {
        try
        {
            await EnsureServicesAsync();

            var collections = await _storage!.GetCollectionsAsync();
            var results = collections.Select(c => new
            {
                source = c.Source, item_count = c.ItemCount,
                with_embeddings = c.WithEmbeddings,
                earliest = c.Earliest.ToString("yyyy-MM-dd"),
                latest = c.Latest.ToString("yyyy-MM-dd"),
                avg_content_length = c.AvgContentLength
            }).ToList();

            return Json(new { success = true, total_collections = results.Count,
                total_items = results.Sum(r => r.item_count), collections = results });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "get_collection_items")]
    [Description(
        "Get items from a specific collection. Returns IDs, titles, URLs, summaries. " +
        "Use list_collections first to see available source names.")]
    public static async Task<string> GetCollectionItemsAsync(
        [Description("Collection source (e.g., 'crawl:docs', 'hn', 'bbc')")]
        string source,
        [Description("Max items (default: 25)")]
        int limit = 25)
    {
        try
        {
            await EnsureServicesAsync();

            var items = await _storage!.GetItemsBySourceAsync(source, limit);
            var results = items.Select(s => new
            {
                id = s.Id, title = s.Title, url = s.Url,
                summary = Truncate(s.Summary ?? s.Content, 200),
                topic = s.DetectedTopic,
                sentiment = Math.Round(s.SentimentScore, 2),
                created_at = s.CreatedAt.ToString("yyyy-MM-dd"),
                has_embedding = s.Embedding != null
            }).ToList();

            return Json(new { success = true, source,
                item_count = results.Count, items = results });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  ENTITY GRAPH
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "list_entities")]
    [Description(
        "List top entities (people, organizations, locations) from the knowledge graph. " +
        "Filter by type (PER, ORG, LOC, MISC) or recency (days back).")]
    public static async Task<string> ListEntitiesAsync(
        [Description("Entity type: 'PER', 'ORG', 'LOC', 'MISC', or omit for all")]
        string? type = null,
        [Description("Only entities seen in last N days (omit for all time)")]
        int? daysBack = null,
        [Description("Max entities (default: 20)")]
        int limit = 20)
    {
        try
        {
            await EnsureServicesAsync();

            var entities = await _storage!.GetTopEntitiesAsync(limit, type, daysBack);
            var results = entities.Select(e => new
            {
                id = e.Id, name = e.Name, type = e.Type,
                mention_count = e.MentionCount, article_count = e.ArticleCount,
                first_seen = e.FirstSeen.ToString("yyyy-MM-dd"),
                last_seen = e.LastSeen.ToString("yyyy-MM-dd"),
                freshness_score = Math.Round(e.FreshnessScore, 2)
            }).ToList();

            return Json(new { success = true, type_filter = type,
                days_back = daysBack, entity_count = results.Count, entities = results });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "get_entity_details")]
    [Description(
        "Get entity details: relationships to other entities and articles mentioning it. " +
        "Use list_entities first to find entity IDs.")]
    public static async Task<string> GetEntityDetailsAsync(
        [Description("Entity ID")]
        string entityId)
    {
        try
        {
            await EnsureServicesAsync();

            var relationships = await _storage!.GetRelationshipsAsync(entityId);
            var articles = await _storage.GetArticlesForEntityAsync(entityId);

            return Json(new
            {
                success = true, entity_id = entityId,
                relationship_count = relationships.Count,
                article_count = articles.Count,
                relationships = relationships.Select(r => new
                {
                    source = r.SourceName, target = r.TargetName,
                    type = r.Type, weight = Math.Round(r.Weight, 1)
                }),
                articles = articles.Select(a => new
                {
                    item_id = a.itemId, title = a.title,
                    url = a.url, confidence = Math.Round(a.confidence, 2)
                })
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "get_entity_network")]
    [Description(
        "Get the local network around one or more entities: " +
        "all relationships between them and their neighbors, plus the articles they co-occur in. " +
        "Useful for understanding how entities connect across your knowledge base.")]
    public static async Task<string> GetEntityNetworkAsync(
        [Description("Comma-separated entity IDs to explore")]
        string entityIds,
        [Description("Max neighbor depth (1 = direct connections only, default: 1)")]
        int depth = 1)
    {
        try
        {
            await EnsureServicesAsync();

            var ids = entityIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (ids.Count == 0)
                return Json(new { success = false, error = "No entity IDs provided" });

            var allRelationships = new List<GraphRelationship>();
            var allEntityIds = new HashSet<string>(ids);
            var articlesPerEntity = new Dictionary<string, List<(string itemId, string title, string? url, double confidence)>>();

            // Get direct relationships and articles for seed entities
            foreach (var id in ids)
            {
                var rels = await _storage!.GetRelationshipsAsync(id);
                allRelationships.AddRange(rels);
                foreach (var r in rels)
                {
                    allEntityIds.Add(r.SourceId);
                    allEntityIds.Add(r.TargetId);
                }

                var articles = await _storage.GetArticlesForEntityAsync(id);
                articlesPerEntity[id] = articles;
            }

            // Deduplicate relationships
            var uniqueRels = allRelationships
                .GroupBy(r => $"{r.SourceId}|{r.TargetId}|{r.Type}")
                .Select(g => g.First())
                .ToList();

            // Find articles where multiple seed entities co-occur
            var coOccurrences = new List<object>();
            if (articlesPerEntity.Count >= 2)
            {
                var articleSets = articlesPerEntity.Values.Select(a => a.Select(x => x.itemId).ToHashSet()).ToList();
                var intersection = articleSets.Aggregate((a, b) => { a.IntersectWith(b); return a; });
                if (intersection.Count > 0)
                {
                    var sharedItems = await _storage!.GetItemsByIdsAsync(intersection.ToList());
                    coOccurrences = sharedItems.Select(s => (object)new
                    {
                        id = s.Id, title = s.Title, url = s.Url
                    }).ToList();
                }
            }

            return Json(new
            {
                success = true,
                seed_entities = ids.Count,
                total_entities_in_network = allEntityIds.Count,
                relationships = uniqueRels.Select(r => new
                {
                    source = r.SourceName, source_id = r.SourceId,
                    target = r.TargetName, target_id = r.TargetId,
                    type = r.Type, weight = Math.Round(r.Weight, 1)
                }),
                relationship_count = uniqueRels.Count,
                co_occurring_articles = coOccurrences,
                articles_per_entity = articlesPerEntity.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(a => new { item_id = a.itemId, title = a.title }).ToList())
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "find_related_by_entities")]
    [Description(
        "Find documents sharing entities with given items (entity co-occurrence). " +
        "Discovers related articles that keyword search would miss.")]
    public static async Task<string> FindRelatedByEntitiesAsync(
        [Description("Comma-separated item IDs")]
        string itemIds,
        [Description("Max related documents (default: 5)")]
        int limit = 5)
    {
        try
        {
            await EnsureServicesAsync();

            var ids = itemIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (ids.Count == 0)
                return Json(new { success = false, error = "No item IDs provided" });

            var relatedIds = await _storage!.FindRelatedByEntitiesAsync(ids, limit: limit);
            if (relatedIds.Count == 0)
                return Json(new { success = true, source_items = ids.Count,
                    related = Array.Empty<object>(), message = "No related documents found via entity co-occurrence." });

            var relatedItems = await _storage.GetItemsByIdsAsync(relatedIds);
            var results = relatedItems.Select(s => new
            {
                id = s.Id, title = s.Title, url = s.Url,
                source = s.Source, summary = Truncate(s.Summary ?? s.Content, 200)
            }).ToList();

            return Json(new { success = true, source_items = ids.Count,
                related_count = results.Count, related = results });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  ANALYTICS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "get_kb_stats")]
    [Description(
        "Knowledge base overview: items, collections, entities, relationships, " +
        "FTS5 index status, keyword corpus size, embedding model availability.")]
    public static async Task<string> GetKbStatsAsync()
    {
        try
        {
            await EnsureServicesAsync();

            var collections = await _storage!.GetCollectionsAsync();
            var (entities, relationships, mentions) = await _storage.GetGraphStatsAsync();
            var corpusSize = await _storage.GetKeywordCorpusSizeAsync();
            var corpus = await _storage.GetKeywordCorpusAsync();
            var ftsEmpty = await _storage.IsFtsIndexEmptyAsync();

            return Json(new
            {
                success = true,
                total_items = collections.Sum(c => c.ItemCount),
                total_collections = collections.Count,
                items_with_embeddings = collections.Sum(c => c.WithEmbeddings),
                entities, relationships, entity_mentions = mentions,
                fts5_indexed = !ftsEmpty,
                keyword_corpus = new { terms = corpus.Count, docs = corpusSize },
                embedding = new { model = "all-MiniLM-L6-v2", dimensions = 384,
                    available = _embedding?.IsSetup ?? false },
                available_tools = 15
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "get_trends")]
    [Description(
        "Trend analysis: topic distribution, sentiment averages, changes over time. " +
        "Shows what's being discussed and how sentiment shifts.")]
    public static async Task<string> GetTrendsAsync(
        [Description("Days to analyze (default: 7)")]
        int days = 7)
    {
        try
        {
            await EnsureServicesAsync();

            var trends = await _storage!.GetTrendAnalysisAsync(days);

            return Json(new
            {
                success = true,
                period_days = days,
                start_date = trends.StartDate.ToString("yyyy-MM-dd"),
                end_date = trends.EndDate.ToString("yyyy-MM-dd"),
                total_items = trends.TotalItems,
                avg_sentiment = Math.Round(trends.AverageSentiment, 3),
                sentiment_change = Math.Round(trends.SentimentChange, 3),
                topics = trends.TopTopics.Select(t => new
                {
                    topic = t.Topic, count = t.Count,
                    avg_sentiment = Math.Round(t.AverageSentiment, 2)
                })
            });
        }
        catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static string Json(object obj) => JsonSerializer.Serialize(obj, JsonOpts);

    private static string? Truncate(string? text, int maxLen)
    {
        if (text == null) return null;
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }
}

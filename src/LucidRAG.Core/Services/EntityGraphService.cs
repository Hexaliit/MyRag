using System.Diagnostics;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.GraphRag;
using Mostlylucid.GraphRag.Extraction;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;
// Use the GraphRag NER service (not DocSummarizer's standalone version)
using OnnxNerService = Mostlylucid.GraphRag.Extraction.OnnxNerService;

namespace LucidRAG.Services;

/// <summary>
///     Service for extracting entities from documents and building the knowledge graph.
///     Delegates to Mostlylucid.GraphRag for sophisticated IDF-based extraction with BERT deduplication.
/// </summary>
public interface IEntityGraphService
{
    /// <summary>
    ///     Extract entities from segments using GraphRag's heuristic extraction
    /// </summary>
    Task<EntityExtractionResult> ExtractAndStoreEntitiesAsync(
        Guid documentId,
        IReadOnlyList<Segment> segments,
        CancellationToken ct = default);

    /// <summary>
    ///     Get graph data for visualization (D3.js format)
    /// </summary>
    Task<GraphData> GetGraphDataAsync(Guid? documentId = null, CancellationToken ct = default);

    /// <summary>
    ///     Get entities related to a search query
    /// </summary>
    Task<IReadOnlyList<EntityInfo>> GetRelatedEntitiesAsync(string query, int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    ///     Store extracted links (URLs, DOIs, arXiv IDs) as graph entities with "links_to" relationships.
    ///     When two documents link to the same URL/DOI, they share a graph entity → implicit relationship.
    /// </summary>
    Task<int> StoreLinkEntitiesAsync(
        Guid documentId,
        IReadOnlyList<ExtractedLink> links,
        CancellationToken ct = default);
}

public record EntityExtractionResult(
    int EntitiesExtracted,
    int RelationshipsCreated,
    TimeSpan ProcessingTime);

public record GraphData(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public record GraphNode(string Id, string Label, string Type, int MentionCount);

public record GraphEdge(string Source, string Target, string Type, float Weight);

public record EntityInfo(string Name, string Type, string? Description, int MentionCount);

public class EntityGraphService : IEntityGraphService, IDisposable
{
    private readonly RagDocumentsConfig _config;
    private readonly RagDocumentsDbContext _db;
    private readonly EmbeddingService _embedder;
    private readonly GraphRagDb _graphDb;
    private readonly ILogger<EntityGraphService> _logger;
    private bool _initialized;
    private OnnxNerService? _nerService; // Not readonly - assigned lazily after downloading models

    public EntityGraphService(
        RagDocumentsDbContext db,
        IOptions<RagDocumentsConfig> config,
        ILogger<EntityGraphService> logger)
    {
        _db = db;
        _config = config.Value;
        _logger = logger;

        // Initialize GraphRag's DuckDB for entity graph storage
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var graphDbPath = Path.Combine(dataDir, "entities.duckdb");

        _graphDb = new GraphRagDb(graphDbPath);
        _embedder = new EmbeddingService();

        // NER service will be created lazily in EnsureInitializedAsync() after downloading models
        _nerService = null;
    }

    public void Dispose()
    {
        _graphDb.Dispose();
        _embedder.Dispose();
        _nerService?.Dispose();
    }

    public async Task<EntityExtractionResult> ExtractAndStoreEntitiesAsync(
        Guid documentId,
        IReadOnlyList<Segment> segments,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Extracting entities from {Count} segments for document {DocumentId}",
            segments.Count, documentId);

        // LOG: Show what text we're extracting from
        var totalChars = segments.Sum(s => s.Text?.Length ?? 0);
        _logger.LogDebug(
            "GraphRAG extracting from {TotalChars} chars across {SegmentCount} segments. First segment text (100 chars): {FirstText}",
            totalChars, segments.Count,
            segments.FirstOrDefault()?.Text?.Substring(0, Math.Min(100, segments.FirstOrDefault()?.Text?.Length ?? 0)));

        // Convert Segments to GraphRag ChunkResults
        var docIdStr = documentId.ToString("N");
        var chunks = segments.Select((s, i) => new ChunkResult(
            $"{docIdStr}_{i}",
            docIdStr,
            s.Text,
            i
        )).ToList();

        // Store document reference in GraphRag
        await _graphDb.UpsertDocumentAsync(docIdStr, $"doc:{documentId}", "", "");

        // Keep ALL chunks in DuckDB for cross-document IDF computation.
        // Removing ClearAllChunksAsync enables entity extraction to consider
        // Clear ALL chunks before processing this document
        // This ensures the entity extractor only processes this document's chunks,
        // not old demo data or chunks from other documents
       // await _graphDb.ClearAllChunksAsync();// all previously processed documents, building a richer knowledge graph.

        // Store chunks with embeddings
        foreach (var chunk in chunks)
        {
            var embedding = await _embedder.EmbedAsync(chunk.Text, ct);
            await _graphDb.InsertChunkAsync(chunk.Id, chunk.DocumentId, chunk.ChunkIndex, chunk.Text, embedding,
                chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        // Choose extraction profile based on content characteristics
        // For short segments (likely images), use General profile for visual content
        // For longer documents, use Technical profile (blog/markdown focus)
        // Future enhancement: detect profile from content.type signal:
        //   - Photo -> General (person, location, event, product)
        //   - Chart/Diagram -> Data (metric, table, category)
        //   - Technical diagram -> Technical (technology, api, pattern)
        EntityProfile profile;
        if (totalChars < 500 && segments.Count <= 3)
        {
            profile = EntityTypeProfiles.General; // Images: person, location, product, concept, event, etc.
            _logger.LogInformation("Using General profile for short content ({Chars} chars, {Segments} segments)",
                totalChars, segments.Count);
        }
        else
        {
            profile = EntityTypeProfiles.Technical; // Documents: technology, framework, library, etc.
            _logger.LogInformation("Using Technical profile for document content ({Chars} chars, {Segments} segments)",
                totalChars, segments.Count);
        }

        // Use ProfileAwareEntityExtractor with OnnxNerService for quality entity extraction
        // NER finds entity spans (WHERE entities are), profile maps them to types (WHAT kind of entity)
        var extractor = new ProfileAwareEntityExtractor(
            _graphDb,
            _embedder,
            profile,
            null, // No LLM needed with NER
            _nerService); // Heuristic mode with NER fallback

        _logger.LogInformation("Extracting entities with {Profile} profile, NER={HasNer}",
            profile.DisplayName, _nerService != null);

        var result = await extractor.ExtractAsync(null, ct);

        // Sync extracted entities to PostgreSQL for relational queries
        await SyncEntitiesToPostgresAsync(documentId, ct);

        sw.Stop();
        _logger.LogInformation(
            "Entity extraction complete: {Entities} entities, {Relationships} relationships in {Time}ms",
            result.EntitiesExtracted, result.RelationshipsExtracted, sw.ElapsedMilliseconds);

        return new EntityExtractionResult(
            result.EntitiesExtracted,
            result.RelationshipsExtracted,
            sw.Elapsed);
    }

    public async Task<GraphData> GetGraphDataAsync(Guid? documentId = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        // Get data from GraphRag DuckDB (faster for graph queries)
        var entities = await _graphDb.GetAllEntitiesAsync();
        var relationships = await _graphDb.GetAllRelationshipsAsync();

        var nodes = entities
            .Select(e => new GraphNode(e.Id, e.Name, e.Type, e.MentionCount))
            .ToList();

        var edges = relationships
            .Select(r => new GraphEdge(r.SourceEntityId, r.TargetEntityId, r.RelationshipType, r.Weight))
            .ToList();

        return new GraphData(nodes, edges);
    }

    public async Task<IReadOnlyList<EntityInfo>> GetRelatedEntitiesAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        // Embed query and search for similar entities
        var queryEmbedding = await _embedder.EmbedAsync(query, ct);
        var chunks = await _graphDb.SearchChunksAsync(queryEmbedding, limit * 2);

        // Get entities mentioned in matching chunks
        var entitySet = new Dictionary<string, EntityResult>();
        foreach (var chunk in chunks)
        {
            var chunkEntities = await _graphDb.GetEntitiesInChunkAsync(chunk.Id);
            foreach (var e in chunkEntities)
                if (!entitySet.ContainsKey(e.Id))
                    entitySet[e.Id] = e;
        }

        return entitySet.Values
            .OrderByDescending(e => e.MentionCount)
            .Take(limit)
            .Select(e => new EntityInfo(e.Name, e.Type, e.Description, e.MentionCount))
            .ToList();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _graphDb.InitializeAsync();
        await _embedder.InitializeAsync();

        // Re-enabled with debug logging to diagnose 0 entity extraction
        if (_nerService == null)
            try
            {
                var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
                var modelsDir = Path.Combine(dataDir, "models", "bert-base-ner");

                // Auto-download NER models from HuggingFace if not present
                var progress = new Progress<string>(msg => _logger.LogInformation("NER: {Message}", msg));
                var downloaded = await NerModelRegistry.EnsureModelDownloadedAsync(
                    modelsDir,
                    NerModelRegistry.BertBaseNer,
                    progress);

                if (downloaded)
                {
                    _nerService = new OnnxNerService(modelsDir, NerModelRegistry.BertBaseNer);
                    await _nerService.InitializeAsync();
                    _logger.LogInformation("NER service initialized (DEBUG MODE: label distribution logging enabled)");
                }
                else
                {
                    _logger.LogWarning("Failed to download NER models, entity extraction will use heuristics");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize NER service, will use heuristic extraction");
                _nerService = null;
            }

        _initialized = true;
    }

    private async Task SyncEntitiesToPostgresAsync(Guid documentId, CancellationToken ct)
    {
        // Get entities from GraphRag DuckDB
        var graphEntities = await _graphDb.GetAllEntitiesAsync();
        var graphRelationships = await _graphDb.GetAllRelationshipsAsync();

        // Batch pre-load: all existing entities and document links in 2 queries (not N)
        var entityLookup = await _db.Entities
            .ToDictionaryAsync(e => e.CanonicalName.ToLower(), e => e, ct);

        var existingLinks = await _db.DocumentEntityLinks
            .Where(l => l.DocumentId == documentId)
            .ToDictionaryAsync(l => l.EntityId, l => l, ct);

        foreach (var ge in graphEntities)
        {
            var nameKey = ge.Name.ToLower();
            Guid entityId;

            if (entityLookup.TryGetValue(nameKey, out var existing))
            {
                entityId = existing.Id;
            }
            else
            {
                var entity = new ExtractedEntity
                {
                    Id = Guid.NewGuid(),
                    CanonicalName = ge.Name,
                    EntityType = ge.Type,
                    Description = ge.Description,
                    Aliases = []
                };
                _db.Entities.Add(entity);
                entityId = entity.Id;
                entityLookup[nameKey] = entity; // Update cache for relationship lookup
            }

            // Create/update document-entity link
            if (!existingLinks.TryGetValue(entityId, out var existingLink))
            {
                _db.DocumentEntityLinks.Add(new DocumentEntityLink
                {
                    DocumentId = documentId,
                    EntityId = entityId,
                    MentionCount = ge.MentionCount,
                    SegmentIds = []
                });
            }
            else
            {
                existingLink.MentionCount = ge.MentionCount;
            }
        }

        // Sync relationships — batch pre-load existing relationships
        var entityIdLookup = entityLookup.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Id);
        var relevantEntityIds = entityIdLookup.Values.ToHashSet();
        var existingRels = await _db.EntityRelationships
            .Where(r => relevantEntityIds.Contains(r.SourceEntityId) && relevantEntityIds.Contains(r.TargetEntityId))
            .ToListAsync(ct);
        var relLookup = existingRels
            .ToDictionary(r => (r.SourceEntityId, r.TargetEntityId, r.RelationshipType), r => r);

        foreach (var gr in graphRelationships)
        {
            if (!entityIdLookup.TryGetValue(gr.SourceName.ToLower(), out var sourceId) ||
                !entityIdLookup.TryGetValue(gr.TargetName.ToLower(), out var targetId))
                continue;

            var relKey = (sourceId, targetId, gr.RelationshipType);
            if (!relLookup.TryGetValue(relKey, out var existingRel))
            {
                _db.EntityRelationships.Add(new EntityRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceEntityId = sourceId,
                    TargetEntityId = targetId,
                    RelationshipType = gr.RelationshipType,
                    Strength = gr.Weight,
                    SourceDocuments = [documentId]
                });
            }
            else
            {
                existingRel.Strength = Math.Max(existingRel.Strength, gr.Weight);
                if (!existingRel.SourceDocuments.Contains(documentId))
                    existingRel.SourceDocuments = [..existingRel.SourceDocuments, documentId];
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> StoreLinkEntitiesAsync(
        Guid documentId,
        IReadOnlyList<ExtractedLink> links,
        CancellationToken ct = default)
    {
        var stored = 0;

        // Batch pre-load: all entities and document links in 2 queries (not 4N)
        var entityLookup = await _db.Entities
            .ToDictionaryAsync(e => e.CanonicalName.ToLower(), e => e, ct);
        var existingDocLinks = await _db.DocumentEntityLinks
            .Where(l => l.DocumentId == documentId)
            .ToDictionaryAsync(l => l.EntityId, l => l, ct);

        // Pre-load doc entity and its existing relationships for link_to creation
        var docEntityName = $"doc:{documentId}";
        entityLookup.TryGetValue(docEntityName.ToLower(), out var docEntity);
        var existingLinksToRels = docEntity != null
            ? await _db.EntityRelationships
                .Where(r => r.SourceEntityId == docEntity.Id && r.RelationshipType == "links_to")
                .ToDictionaryAsync(r => r.TargetEntityId, r => r, ct)
            : new Dictionary<Guid, EntityRelationship>();

        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();

            var entityType = link.Type switch
            {
                LinkType.Doi => "doi_reference",
                LinkType.ArxivId => "arxiv_reference",
                _ => "external_link"
            };

            var canonicalName = link.Type switch
            {
                LinkType.Doi => $"doi:{link.Value}",
                LinkType.ArxivId => $"arxiv:{link.Value}",
                _ => link.Value
            };

            var nameKey = canonicalName.ToLower();
            Guid entityId;

            if (entityLookup.TryGetValue(nameKey, out var existing))
            {
                entityId = existing.Id;
            }
            else
            {
                var entity = new ExtractedEntity
                {
                    Id = Guid.NewGuid(),
                    CanonicalName = canonicalName,
                    EntityType = entityType,
                    Description = link.Context.Length > 200 ? link.Context[..200] : link.Context,
                    Aliases = []
                };
                _db.Entities.Add(entity);
                entityId = entity.Id;
                entityLookup[nameKey] = entity;
            }

            // Create/update document-entity link
            if (!existingDocLinks.TryGetValue(entityId, out var existingLink))
            {
                var newLink = new DocumentEntityLink
                {
                    DocumentId = documentId,
                    EntityId = entityId,
                    MentionCount = 1,
                    SegmentIds = []
                };
                _db.DocumentEntityLinks.Add(newLink);
                existingDocLinks[entityId] = newLink;
            }
            else
            {
                existingLink.MentionCount++;
            }

            // Create "links_to" relationship from doc entity to link entity
            if (docEntity != null && !existingLinksToRels.ContainsKey(entityId))
            {
                var rel = new EntityRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceEntityId = docEntity.Id,
                    TargetEntityId = entityId,
                    RelationshipType = "links_to",
                    Strength = 0.8f,
                    SourceDocuments = [documentId]
                };
                _db.EntityRelationships.Add(rel);
                existingLinksToRels[entityId] = rel;
            }

            stored++;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Stored {LinkCount} link entities for document {DocumentId}",
            stored, documentId);

        return stored;
    }
}
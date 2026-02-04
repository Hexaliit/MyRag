using System.Collections.Concurrent;
using DoomSummarizer.Services;
using DoomWriter.Models;

namespace DoomWriter.Services;

public class EntityGraphService
{
    private readonly IEntityGraphStore _entityGraph;
    private readonly ConcurrentDictionary<string, byte> _expandedNodeIds = new();
    private readonly NerService _ner;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EntityGraphService(IEntityGraphStore entityGraph, NerService ner)
    {
        _entityGraph = entityGraph;
        _ner = ner;
    }

    public async Task<GraphData> BuildDocumentGraphAsync(
        string documentId, string title, List<TrackedEntity> entities)
    {
        _expandedNodeIds.Clear();

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Document node
        nodes.Add(new GraphNode(
            documentId,
            title,
            "document",
            null,
            1,
            true));
        _expandedNodeIds.TryAdd(documentId, 0);

        // Entity nodes from current document analysis
        foreach (var entity in entities)
        {
            var entityId = KnowledgeGraphService.GenerateEntityId(entity.Name, entity.Type);
            if (_expandedNodeIds.TryAdd(entityId, 0))
                nodes.Add(new GraphNode(
                    entityId,
                    entity.Name,
                    "entity",
                    entity.Type,
                    entity.MentionCount));

            edges.Add(new GraphEdge(
                entityId,
                documentId,
                "mentions",
                entity.MentionCount));
        }

        // Add co-occurrence edges between entities in this document
        var entityIds = entities
            .Select(e => KnowledgeGraphService.GenerateEntityId(e.Name, e.Type))
            .Distinct()
            .ToList();

        for (var i = 0; i < entityIds.Count; i++)
        for (var j = i + 1; j < entityIds.Count; j++)
            edges.Add(new GraphEdge(
                entityIds[i],
                entityIds[j],
                "co_occurs",
                0.5f));

        return new GraphData(nodes, edges);
    }

    public async Task<GraphData> ExpandEntityAsync(string entityId)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Get articles that mention this entity
        var articles = await _entityGraph.GetArticlesForEntityAsync(entityId);
        foreach (var (itemId, title, url, confidence) in articles)
        {
            if (_expandedNodeIds.TryAdd(itemId, 0))
                nodes.Add(new GraphNode(
                    itemId,
                    title,
                    "document",
                    null,
                    1,
                    Url: url));

            edges.Add(new GraphEdge(
                entityId,
                itemId,
                "mentions",
                (float)confidence));
        }

        // Get co-occurring entities
        var relationships = await _entityGraph.GetRelationshipsAsync(entityId);
        foreach (var rel in relationships.Take(15)) // Limit to avoid graph explosion
        {
            var neighborId = rel.SourceId == entityId ? rel.TargetId : rel.SourceId;
            var neighborName = rel.SourceId == entityId ? rel.TargetName : rel.SourceName;

            if (_expandedNodeIds.TryAdd(neighborId, 0))
            {
                // Determine entity type from the ID prefix
                var entityType = neighborId.Split('_')[0].ToUpperInvariant() switch
                {
                    "PER" => "PER",
                    "ORG" => "ORG",
                    "LOC" => "LOC",
                    _ => "MISC"
                };

                nodes.Add(new GraphNode(
                    neighborId,
                    neighborName,
                    "entity",
                    entityType,
                    (int)rel.Weight));
            }

            edges.Add(new GraphEdge(
                rel.SourceId,
                rel.TargetId,
                rel.Type,
                rel.Weight));
        }

        return new GraphData(nodes, edges);
    }

    public async Task<GraphData> ExpandDocumentAsync(string documentId)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Get entities for this document from the graph store
        var entityItems = await _entityGraph.GetEntitiesForItemAsync(documentId);
        foreach (var (entityId, name, confidence, mentions) in entityItems)
        {
            if (_expandedNodeIds.TryAdd(entityId, 0))
            {
                var entityType = entityId.Split('_')[0].ToUpperInvariant() switch
                {
                    "PER" => "PER",
                    "ORG" => "ORG",
                    "LOC" => "LOC",
                    _ => "MISC"
                };

                nodes.Add(new GraphNode(
                    entityId,
                    name,
                    "entity",
                    entityType,
                    mentions));
            }

            edges.Add(new GraphEdge(
                entityId,
                documentId,
                "mentions",
                confidence));
        }

        return new GraphData(nodes, edges);
    }

    public async Task PersistDocumentEntitiesAsync(
        string documentId, string title, List<TrackedEntity> entities)
    {
        await _writeLock.WaitAsync();
        try
        {
            var entityIds = new List<string>();

            foreach (var entity in entities)
            {
                var entityId = KnowledgeGraphService.GenerateEntityId(entity.Name, entity.Type);
                entityIds.Add(entityId);

                await _entityGraph.UpsertEntityAsync(
                    entityId, entity.Name, entity.Type, 0.8);
                await _entityGraph.UpsertEntityMentionAsync(
                    entityId, documentId, 0.8, title);
            }

            // Co-occurrence edges
            for (var i = 0; i < entityIds.Count; i++)
            for (var j = i + 1; j < entityIds.Count; j++)
                await _entityGraph.UpsertRelationshipAsync(
                    entityIds[i], entityIds[j]);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
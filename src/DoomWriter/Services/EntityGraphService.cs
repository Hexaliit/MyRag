using DoomSummarizer.Models;
using DoomSummarizer.Services;
using DoomWriter.Models;

namespace DoomWriter.Services;

public class EntityGraphService
{
    private readonly IEntityGraphStore _entityGraph;
    private readonly NerService _ner;
    private readonly HashSet<string> _expandedNodeIds = new();
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
            Id: documentId,
            Label: title,
            NodeType: "document",
            EntityType: null,
            Weight: 1,
            IsCurrentDocument: true));
        _expandedNodeIds.Add(documentId);

        // Entity nodes from current document analysis
        foreach (var entity in entities)
        {
            var entityId = KnowledgeGraphService.GenerateEntityId(entity.Name, entity.Type);
            if (_expandedNodeIds.Add(entityId))
            {
                nodes.Add(new GraphNode(
                    Id: entityId,
                    Label: entity.Name,
                    NodeType: "entity",
                    EntityType: entity.Type,
                    Weight: entity.MentionCount));
            }

            edges.Add(new GraphEdge(
                Source: entityId,
                Target: documentId,
                EdgeType: "mentions",
                Weight: entity.MentionCount));
        }

        // Add co-occurrence edges between entities in this document
        var entityIds = entities
            .Select(e => KnowledgeGraphService.GenerateEntityId(e.Name, e.Type))
            .Distinct()
            .ToList();

        for (int i = 0; i < entityIds.Count; i++)
        {
            for (int j = i + 1; j < entityIds.Count; j++)
            {
                edges.Add(new GraphEdge(
                    Source: entityIds[i],
                    Target: entityIds[j],
                    EdgeType: "co_occurs",
                    Weight: 0.5f));
            }
        }

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
            if (_expandedNodeIds.Add(itemId))
            {
                nodes.Add(new GraphNode(
                    Id: itemId,
                    Label: title,
                    NodeType: "document",
                    EntityType: null,
                    Weight: 1,
                    Url: url));
            }

            edges.Add(new GraphEdge(
                Source: entityId,
                Target: itemId,
                EdgeType: "mentions",
                Weight: (float)confidence));
        }

        // Get co-occurring entities
        var relationships = await _entityGraph.GetRelationshipsAsync(entityId);
        foreach (var rel in relationships.Take(15)) // Limit to avoid graph explosion
        {
            var neighborId = rel.SourceId == entityId ? rel.TargetId : rel.SourceId;
            var neighborName = rel.SourceId == entityId ? rel.TargetName : rel.SourceName;

            if (_expandedNodeIds.Add(neighborId))
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
                    Id: neighborId,
                    Label: neighborName,
                    NodeType: "entity",
                    EntityType: entityType,
                    Weight: (int)rel.Weight));
            }

            edges.Add(new GraphEdge(
                Source: rel.SourceId,
                Target: rel.TargetId,
                EdgeType: rel.Type,
                Weight: rel.Weight));
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
            if (_expandedNodeIds.Add(entityId))
            {
                var entityType = entityId.Split('_')[0].ToUpperInvariant() switch
                {
                    "PER" => "PER",
                    "ORG" => "ORG",
                    "LOC" => "LOC",
                    _ => "MISC"
                };

                nodes.Add(new GraphNode(
                    Id: entityId,
                    Label: name,
                    NodeType: "entity",
                    EntityType: entityType,
                    Weight: mentions));
            }

            edges.Add(new GraphEdge(
                Source: entityId,
                Target: documentId,
                EdgeType: "mentions",
                Weight: confidence));
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
            for (int i = 0; i < entityIds.Count; i++)
            {
                for (int j = i + 1; j < entityIds.Count; j++)
                {
                    await _entityGraph.UpsertRelationshipAsync(
                        entityIds[i], entityIds[j], "co_occurs");
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

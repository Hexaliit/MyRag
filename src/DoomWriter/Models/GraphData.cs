namespace DoomWriter.Models;

public record GraphData(List<GraphNode> Nodes, List<GraphEdge> Edges);

public record GraphNode(
    string Id,
    string Label,
    string NodeType, // "entity" or "document"
    string? EntityType, // PER/ORG/LOC/MISC (null for documents)
    int Weight, // mention count / importance
    bool IsCurrentDocument = false,
    string? Url = null);

public record GraphEdge(
    string Source,
    string Target,
    string EdgeType, // "mentions" or "co_occurs"
    float Weight = 1f);

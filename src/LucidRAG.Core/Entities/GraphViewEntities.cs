namespace LucidRAG.Entities;

/// <summary>
///     Materialized view: precomputed document-to-document relationships via shared entities.
///     Backed by mv_document_graph.
/// </summary>
public class DocumentGraphEdge
{
    public Guid SourceDocumentId { get; set; }
    public Guid TargetDocumentId { get; set; }
    public long SharedEntityCount { get; set; }
    public string SharedEntityNames { get; set; } = "";
}

/// <summary>
///     Materialized view: precomputed entity importance via degree centrality.
///     Backed by mv_entity_centrality.
/// </summary>
public class EntityCentrality
{
    public Guid EntityId { get; set; }
    public string CanonicalName { get; set; } = "";
    public string EntityType { get; set; } = "";
    public long OutDegree { get; set; }
    public long InDegree { get; set; }
    public long TotalDegree { get; set; }
    public long DocumentCount { get; set; }
}

/// <summary>
///     Result type for the graph_walk() PostgreSQL function.
/// </summary>
public class GraphWalkResult
{
    public Guid EntityId { get; set; }
    public string CanonicalName { get; set; } = "";
    public string EntityType { get; set; } = "";
    public int Depth { get; set; }
    public string RelationshipType { get; set; } = "";
}

/// <summary>
///     Result type for the document_similarity() PostgreSQL function.
/// </summary>
public class DocumentSimilarityResult
{
    public Guid DocumentId { get; set; }
    public string DocumentName { get; set; } = "";
    public long SharedEntityCount { get; set; }
    public string SharedEntityNames { get; set; } = "";
}

/// <summary>
///     Result type for the find_shortest_path() PostgreSQL function.
/// </summary>
public class ShortestPathStep
{
    public Guid EntityId { get; set; }
    public string EntityName { get; set; } = "";
    public string RelationshipType { get; set; } = "";
    public int Depth { get; set; }
}

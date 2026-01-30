namespace LucidRAG.Decomposer.Models;

/// <summary>
/// Root result of the decomposition pipeline.
/// Contains the query tree, execution plan, and metadata for the orchestrator.
/// </summary>
public record DecompositionResult
{
    /// <summary>Original user query.</summary>
    public required string OriginalQuery { get; init; }

    /// <summary>
    /// Root nodes of the decomposition tree.
    /// For simple queries, this is a single Atomic node.
    /// For complex queries, multiple nodes with children.
    /// </summary>
    public List<QueryNode> Nodes { get; init; } = [];

    /// <summary>Execution plan derived from the node tree.</summary>
    public ExecutionPlan Plan { get; init; } = new();

    /// <summary>Query complexity classification (fast-path routing).</summary>
    public QueryComplexity Complexity { get; init; } = QueryComplexity.Simple;

    /// <summary>Overall detected concept type.</summary>
    public ConceptType Concept { get; init; } = ConceptType.General;

    /// <summary>Aggregated signals from Phase 1 analysis.</summary>
    public QuerySignals? Signals { get; init; }

    /// <summary>
    /// True if this query should take the fast path (no decomposition).
    /// Determined by: single topic, no references, no comparisons, no tool actions.
    /// </summary>
    public bool IsFastPath => Complexity == QueryComplexity.Simple && !HasToolActions;

    /// <summary>Whether the plan includes tool action nodes.</summary>
    public bool HasToolActions => Plan.ToolActionNodes.Count > 0;

    /// <summary>Total leaf nodes across the tree.</summary>
    public int LeafCount => Nodes.Sum(CountLeaves);

    private static int CountLeaves(QueryNode node) =>
        node.Children.Count == 0 ? 1 : node.Children.Sum(CountLeaves);
}

/// <summary>
/// Execution strategy for the decomposition result.
/// Tells the orchestrator what to run and in what order.
/// </summary>
public record ExecutionPlan
{
    /// <summary>Prerequisite nodes that must complete first (KnowledgeGap).</summary>
    public List<QueryNode> Prerequisites { get; init; } = [];

    /// <summary>Independent nodes that can execute in parallel.</summary>
    public List<QueryNode> ParallelNodes { get; init; } = [];

    /// <summary>Nodes that depend on other nodes completing first.</summary>
    public List<DependentExecution> DependentNodes { get; init; } = [];

    /// <summary>Content reference nodes (direct fetch, no search).</summary>
    public List<QueryNode> ReferenceNodes { get; init; } = [];

    /// <summary>
    /// Tool action nodes — executed via tool executors, not the default search pipeline.
    /// These may be sequential (file find → index → query) or parallel.
    /// </summary>
    public List<QueryNode> ToolActionNodes { get; init; } = [];

    /// <summary>Max allowed leaf nodes (budget).</summary>
    public int MaxLeaves { get; init; } = 8;

    /// <summary>Max recursion depth.</summary>
    public int MaxDepth { get; init; } = 3;
}

/// <summary>
/// A node that depends on other nodes completing before it can execute.
/// </summary>
public record DependentExecution
{
    public required QueryNode Node { get; init; }
    public required List<string> DependsOnNodeIds { get; init; }
}

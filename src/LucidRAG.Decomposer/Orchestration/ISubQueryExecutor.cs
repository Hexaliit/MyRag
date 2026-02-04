using LucidRAG.Decomposer.Models;

namespace LucidRAG.Decomposer.Orchestration;

/// <summary>
///     Abstraction for sub-query execution. Implemented by DoomSummarizer.Core
///     wrapping the existing fetch + score pipeline.
///     The decomposer does NOT execute queries itself  -  it only plans and orchestrates.
/// </summary>
public interface ISubQueryExecutor
{
    /// <summary>
    ///     Execute a sub-query: fetch sources, score items, return ranked results.
    /// </summary>
    Task<SubQueryResult> ExecuteAsync(QueryNode node, CancellationToken ct = default);

    /// <summary>
    ///     Fetch content from a direct reference (URL, file, DOI).
    /// </summary>
    Task<SubQueryResult> FetchReferenceAsync(ContentReference reference, CancellationToken ct = default);

    /// <summary>
    ///     Execute a tool action (file system, indexer, KB query, analyze, crawl, etc.).
    ///     Returns false from SupportsToolAsync if the executor doesn't handle this tool kind.
    /// </summary>
    Task<SubQueryResult> ExecuteToolAsync(QueryNode node, ToolAction action, CancellationToken ct = default);

    /// <summary>
    ///     Whether this executor supports a given tool kind.
    ///     Executors should return false for unsupported tools so the orchestrator
    ///     can fall back or skip gracefully.
    /// </summary>
    Task<bool> SupportsToolAsync(ToolKind tool, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }
}

/// <summary>
///     Result from executing a single sub-query.
/// </summary>
public record SubQueryResult
{
    /// <summary>The node that was executed.</summary>
    public required string NodeId { get; init; }

    /// <summary>Whether execution succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Result items (opaque to decomposer  -  typed by consumer).</summary>
    public object? Items { get; init; }

    /// <summary>Number of items returned.</summary>
    public int ItemCount { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Pre-computed embedding of the result content (for caching).</summary>
    public float[]? ResultEmbedding { get; init; }
}
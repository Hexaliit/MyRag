using LucidRAG.Decomposer.Caching;
using LucidRAG.Decomposer.KnowledgeBase;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Orchestration;

/// <summary>
/// Executes the decomposition plan: prerequisites first, then parallel execution
/// with cache checks, KB probing, and optional recursion.
/// Does NOT execute queries itself  -  delegates to ISubQueryExecutor.
/// </summary>
public class DecompositionOrchestrator
{
    private readonly IDecompositionCache? _cache;
    private readonly IKnowledgeBaseProbe? _kbProbe;
    private readonly ILogger<DecompositionOrchestrator>? _logger;

    public DecompositionOrchestrator(
        IDecompositionCache? cache = null,
        IKnowledgeBaseProbe? kbProbe = null,
        ILogger<DecompositionOrchestrator>? logger = null)
    {
        _cache = cache;
        _kbProbe = kbProbe;
        _logger = logger;
    }

    /// <summary>
    /// Execute a decomposition plan.
    /// </summary>
    public async Task<AggregatedResult> ExecuteAsync(
        DecompositionResult plan,
        ISubQueryExecutor executor,
        CancellationToken ct = default)
    {
        var guard = new RecursionGuard();
        var results = new Dictionary<string, SubQueryResult>();

        // FAST PATH: single-node queries skip all orchestration overhead
        if (plan.IsFastPath && plan.Nodes.Count == 1)
        {
            _logger?.LogDebug("Fast path: single-node query, skipping orchestration");
            var result = await ExecuteNodeAsync(plan.Nodes[0], executor, guard, ct);
            results[plan.Nodes[0].Id] = result;

            return new AggregatedResult
            {
                OriginalQuery = plan.OriginalQuery,
                NodeResults = results,
                Concept = plan.Concept,
                WasFastPath = true
            };
        }

        // PHASE A: Execute prerequisites (KnowledgeGap nodes)
        if (plan.Plan.Prerequisites.Count > 0)
        {
            _logger?.LogDebug("Executing {Count} prerequisite nodes", plan.Plan.Prerequisites.Count);
            var prereqTasks = plan.Plan.Prerequisites.Select(async node =>
            {
                var result = await ExecuteNodeAsync(node, executor, guard, ct);
                return (node.Id, result);
            });

            foreach (var (id, result) in await Task.WhenAll(prereqTasks))
                results[id] = result;
        }

        // PHASE B: Execute content reference nodes (direct fetch)
        if (plan.Plan.ReferenceNodes.Count > 0)
        {
            _logger?.LogDebug("Fetching {Count} content references", plan.Plan.ReferenceNodes.Count);
            var refTasks = plan.Plan.ReferenceNodes.Select(async node =>
            {
                var result = node.Reference != null
                    ? await executor.FetchReferenceAsync(node.Reference, ct)
                    : new SubQueryResult { NodeId = node.Id, Success = false, Error = "No reference" };

                node.Status = result.Success ? QueryNodeStatus.Complete : QueryNodeStatus.Failed;
                return (node.Id, result);
            });

            foreach (var (id, result) in await Task.WhenAll(refTasks))
                results[id] = result;
        }

        // PHASE C: Execute tool action nodes (may be sequential chains or parallel)
        if (plan.Plan.ToolActionNodes.Count > 0)
        {
            _logger?.LogDebug("Executing {Count} tool action nodes", plan.Plan.ToolActionNodes.Count);
            foreach (var node in plan.Plan.ToolActionNodes)
            {
                // Tool chains are often sequential (find files → index → query)
                // Check if this tool node depends on a previous result
                if (node.ParentId != null && results.TryGetValue(node.ParentId, out var parentResult)
                    && !parentResult.Success)
                {
                    results[node.Id] = new SubQueryResult
                    {
                        NodeId = node.Id,
                        Success = false,
                        Error = "Tool chain dependency failed"
                    };
                    continue;
                }

                var result = await ExecuteToolNodeAsync(node, executor, ct);
                results[node.Id] = result;
            }
        }

        // PHASE D: Execute parallel nodes (with cache + KB checks)
        if (plan.Plan.ParallelNodes.Count > 0)
        {
            _logger?.LogDebug("Executing {Count} parallel nodes", plan.Plan.ParallelNodes.Count);
            var parallelTasks = plan.Plan.ParallelNodes.Select(async node =>
            {
                var result = await ExecuteNodeAsync(node, executor, guard, ct);
                return (node.Id, result);
            });

            foreach (var (id, result) in await Task.WhenAll(parallelTasks))
                results[id] = result;
        }

        // PHASE E: Execute dependent nodes
        foreach (var dep in plan.Plan.DependentNodes)
        {
            // Wait for dependencies (already completed in earlier phases)
            var allDepsComplete = dep.DependsOnNodeIds.All(id =>
                results.ContainsKey(id) && results[id].Success);

            if (allDepsComplete)
            {
                var result = await ExecuteNodeAsync(dep.Node, executor, guard, ct);
                results[dep.Node.Id] = result;
            }
            else
            {
                results[dep.Node.Id] = new SubQueryResult
                {
                    NodeId = dep.Node.Id,
                    Success = false,
                    Error = "Dependencies not satisfied"
                };
            }
        }

        return new AggregatedResult
        {
            OriginalQuery = plan.OriginalQuery,
            NodeResults = results,
            Concept = plan.Concept,
            WasFastPath = false
        };
    }

    private async Task<SubQueryResult> ExecuteToolNodeAsync(
        QueryNode node,
        ISubQueryExecutor executor,
        CancellationToken ct)
    {
        if (node.ToolAction == null)
        {
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = "Tool action node missing ToolAction"
            };
        }

        // Check tool support
        var supported = await executor.SupportsToolAsync(node.ToolAction.Tool, ct);
        if (!supported)
        {
            _logger?.LogWarning("Executor does not support tool {Tool}, skipping: {Query}",
                node.ToolAction.Tool, node.Query);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = $"Tool '{node.ToolAction.Tool}' not supported by executor"
            };
        }

        node.Status = QueryNodeStatus.Executing;
        try
        {
            var result = await executor.ExecuteToolAsync(node, node.ToolAction, ct);
            node.Status = result.Success ? QueryNodeStatus.Complete : QueryNodeStatus.Failed;
            _logger?.LogDebug("Tool {Tool} execution {Status}: {Query}",
                node.ToolAction.Tool, node.Status, node.Query);
            return result;
        }
        catch (Exception ex)
        {
            node.Status = QueryNodeStatus.Failed;
            _logger?.LogWarning(ex, "Tool execution failed for {Tool}: {Query}",
                node.ToolAction.Tool, node.Query);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private async Task<SubQueryResult> ExecuteNodeAsync(
        QueryNode node,
        ISubQueryExecutor executor,
        RecursionGuard guard,
        CancellationToken ct)
    {
        // 1. CHECK CACHE
        if (_cache != null && node.Embedding != null)
        {
            var cached = await _cache.GetAsync(node.Embedding, node.TimeScope, ct);
            if (cached != null)
            {
                node.Status = QueryNodeStatus.Cached;
                node.CacheResult = cached;
                _logger?.LogDebug("Cache hit (sim={Sim:F3}) for: {Query}", cached.Similarity, node.Query);
                return new SubQueryResult
                {
                    NodeId = node.Id,
                    Success = true,
                    Items = cached.CachedResult,
                    ItemCount = 0
                };
            }
        }

        // 2. CHECK KB
        if (_kbProbe != null)
        {
            var kbResult = await _kbProbe.ProbeAsync(node, ct);
            node.KbResult = kbResult;

            if (kbResult.Status == KbProbeStatus.Satisfied)
            {
                node.Status = QueryNodeStatus.KbSatisfied;
                _logger?.LogDebug("KB satisfied (score={Score:F2}) for: {Query}",
                    kbResult.TopScore, node.Query);
                // Still execute but orchestrator can reduce fetch limits
            }
        }

        // 3. EXECUTE
        node.Status = QueryNodeStatus.Executing;
        guard.RegisterLeaf();

        try
        {
            var result = await executor.ExecuteAsync(node, ct);
            node.Status = result.Success ? QueryNodeStatus.Complete : QueryNodeStatus.Failed;

            // 4. CACHE RESULT
            if (_cache != null && result.Success && node.Embedding != null)
            {
                await _cache.PutAsync(node.Embedding, node.TimeScope, result, ct);
            }

            return result;
        }
        catch (Exception ex)
        {
            node.Status = QueryNodeStatus.Failed;
            _logger?.LogWarning(ex, "Node execution failed for: {Query}", node.Query);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }
}

/// <summary>
/// Aggregated results from all sub-query executions.
/// </summary>
public record AggregatedResult
{
    public required string OriginalQuery { get; init; }
    public Dictionary<string, SubQueryResult> NodeResults { get; init; } = new();
    public ConceptType Concept { get; init; } = ConceptType.General;
    public bool WasFastPath { get; init; }
    public int TotalItems => NodeResults.Values.Sum(r => r.ItemCount);
    public int SuccessfulNodes => NodeResults.Values.Count(r => r.Success);
    public int FailedNodes => NodeResults.Values.Count(r => !r.Success);
}

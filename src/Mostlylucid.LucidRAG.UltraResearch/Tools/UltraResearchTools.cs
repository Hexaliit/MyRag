using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace LucidRAG.UltraResearch.Tools;

/// <summary>
///     MCP tools exposing UltraResearch's autonomous research corpus builder
///     to AI agents via the Model Context Protocol.
///     Tool categories:
///     Session:   ultraresearch_start, ultraresearch_stop, ultraresearch_status, ultraresearch_list_sessions
///     Frontier:  ultraresearch_frontier
///     Analysis:  ultraresearch_checkpoints, ultraresearch_stats
///     All tools return JSON. Must be configured via <see cref="Configure" /> before use —
///     the orchestrator requires database access provided by the host application.
/// </summary>
[McpServerToolType]
public static class UltraResearchTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static UltraResearchOrchestrator? _orchestrator;
    private static IServiceProvider? _services;

    /// <summary>
    ///     Tracks sessions started via MCP so we can list/manage them.
    ///     Maps sessionId to config used at start time.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, UltraResearchConfig> McpSessions = new();

    /// <summary>
    ///     Configure the MCP tools with a pre-built orchestrator and service provider.
    ///     Must be called by the host application before any tools are invoked.
    ///     The orchestrator requires RagDocumentsDbContext and scoped services
    ///     that cannot be self-initialized in standalone MCP mode.
    /// </summary>
    public static void Configure(UltraResearchOrchestrator orchestrator, IServiceProvider services)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Returns null if configured, or a JSON error string if not.</summary>
    private static string? CheckConfigured()
    {
        if (_orchestrator != null) return null;

        return Json(new
        {
            success = false,
            error = "UltraResearch MCP tools are not configured.",
            message = "These tools require the full lucidrag CLI with database access. " +
                      "Run 'lucidrag --mcp' instead of 'doomsummarizer --mcp' to use UltraResearch tools, " +
                      "or use the lucidrag web application which registers all required services."
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  SESSION MANAGEMENT
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "ultraresearch_start")]
    [Description(
        "Start an autonomous UltraResearch session that discovers, fetches, and indexes " +
        "academic papers from arXiv and Semantic Scholar. " +
        "The session runs in the background, building a research corpus via citation graph " +
        "traversal with LLM sentinel checkpoints for convergence detection. " +
        "Returns a session ID for monitoring with ultraresearch_status.")]
    public static async Task<string> StartResearchAsync(
        [Description("Research topic query (e.g., 'retrieval augmented generation', 'transformer architectures')")]
        string topic,
        [Description("Max papers to fetch before stopping (default: 200)")]
        int maxPapers = 200,
        [Description("Papers to fetch per iteration before analyzing (default: 10)")]
        int batchSize = 10,
        [Description("Max main loop iterations (default: 50)")]
        int maxIterations = 50,
        [Description("Comma-separated seed arXiv IDs to start from (optional)")]
        string? seedArxivIds = null,
        [Description("Comma-separated seed DOIs to start from (optional)")]
        string? seedDois = null,
        [Description("Dry-run mode: search + discover without ingesting (default: false)")]
        bool dryRun = false,
        [Description("Use Semantic Scholar for reverse citations (default: true)")]
        bool includeSemanticScholar = true)
    {
        try
        {
            if (CheckConfigured() is { } err) return err;

            var config = new UltraResearchConfig
            {
                Topic = topic,
                MaxPapers = Math.Clamp(maxPapers, 1, 10000),
                BatchSize = Math.Clamp(batchSize, 1, 50),
                MaxIterations = Math.Clamp(maxIterations, 1, 500),
                DryRun = dryRun,
                IncludeSemanticScholar = includeSemanticScholar
            };

            if (!string.IsNullOrWhiteSpace(seedArxivIds))
            {
                config.SeedArxivIds = seedArxivIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(seedDois))
            {
                config.SeedDois = seedDois
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            // Resolve ingester from host DI — required for non-dry-run mode
            IDocumentIngester? ingester = _services!.GetService(typeof(IDocumentIngester)) as IDocumentIngester;
            if (ingester == null && !dryRun)
            {
                return Json(new
                {
                    success = false,
                    error = "No document ingester available. Use dryRun=true for discovery-only mode, " +
                            "or ensure IDocumentIngester is registered in the host application."
                });
            }

            ingester ??= new NoOpIngester();

            var sessionId = await _orchestrator!.StartAsync(config, ingester);

            McpSessions[sessionId] = config;

            return Json(new
            {
                success = true,
                session_id = sessionId,
                topic,
                max_papers = config.MaxPapers,
                batch_size = config.BatchSize,
                max_iterations = config.MaxIterations,
                dry_run = dryRun,
                seed_arxiv_ids = config.SeedArxivIds.Count,
                seed_dois = config.SeedDois.Count,
                message = dryRun
                    ? "Dry-run session started. Papers will be discovered but not ingested."
                    : "Research session started. Use ultraresearch_status to monitor progress."
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "ultraresearch_stop")]
    [Description(
        "Gracefully stop a running UltraResearch session. " +
        "The session will finish its current operation and persist state.")]
    public static Task<string> StopResearchAsync(
        [Description("Session ID returned by ultraresearch_start")]
        string sessionId)
    {
        try
        {
            if (CheckConfigured() is { } err) return Task.FromResult(err);

            if (!Guid.TryParse(sessionId, out var id))
                return Task.FromResult(Json(new { success = false, error = "Invalid session ID format" }));

            var status = _orchestrator!.GetStatus(id);
            if (status == null)
                return Task.FromResult(Json(new
                    { success = false, error = $"Session {sessionId} not found or already completed" }));

            _orchestrator.Stop(id);

            return Task.FromResult(Json(new
            {
                success = true,
                session_id = id,
                message = "Stop requested. Session will finish current operation and persist state.",
                last_status = new
                {
                    status = status.Status.ToString(),
                    papers_fetched = status.PapersFetched,
                    papers_ingested = status.PapersIngested,
                    iteration = status.Iteration
                }
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Json(new { success = false, error = ex.Message }));
        }
    }

    [McpServerTool(Name = "ultraresearch_status")]
    [Description(
        "Get the current status of an UltraResearch session: progress counters, " +
        "frontier size, checkpoint count, and stop reason if completed.")]
    public static Task<string> GetSessionStatusAsync(
        [Description("Session ID returned by ultraresearch_start")]
        string sessionId)
    {
        try
        {
            if (CheckConfigured() is { } err) return Task.FromResult(err);

            if (!Guid.TryParse(sessionId, out var id))
                return Task.FromResult(Json(new { success = false, error = "Invalid session ID format" }));

            var status = _orchestrator!.GetStatus(id);
            if (status == null)
                return Task.FromResult(Json(new { success = false, error = $"Session {sessionId} not found" }));

            return Task.FromResult(Json(new
            {
                success = true,
                session_id = status.SessionId,
                collection_id = status.CollectionId,
                status = status.Status.ToString(),
                topic = status.Topic,
                iteration = status.Iteration,
                papers_fetched = status.PapersFetched,
                papers_ingested = status.PapersIngested,
                papers_failed = status.PapersFailed,
                frontier_size = status.FrontierSize,
                seen_ids = status.SeenIdsCount,
                checkpoints = status.CheckpointCount,
                started_at = status.StartedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                completed_at = status.CompletedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                stop_reason = status.StopReason,
                is_running = status.Status == UltraResearchStatus.Running
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Json(new { success = false, error = ex.Message }));
        }
    }

    [McpServerTool(Name = "ultraresearch_list_sessions")]
    [Description(
        "List all UltraResearch sessions started via MCP in this process, " +
        "with their current status and progress.")]
    public static Task<string> ListSessionsAsync()
    {
        try
        {
            if (CheckConfigured() is { } err) return Task.FromResult(err);

            var sessions = McpSessions.ToList();

            var results = new List<object>();
            foreach (var (sessionId, config) in sessions)
            {
                var status = _orchestrator!.GetStatus(sessionId);
                results.Add(new
                {
                    session_id = sessionId,
                    topic = config.Topic,
                    status = status?.Status.ToString() ?? "completed",
                    papers_fetched = status?.PapersFetched ?? 0,
                    papers_ingested = status?.PapersIngested ?? 0,
                    frontier_size = status?.FrontierSize ?? 0,
                    iteration = status?.Iteration ?? 0,
                    is_running = status?.Status == UltraResearchStatus.Running,
                    stop_reason = status?.StopReason
                });
            }

            return Task.FromResult(Json(new
            {
                success = true,
                session_count = results.Count,
                sessions = results
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Json(new { success = false, error = ex.Message }));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  FRONTIER & ANALYSIS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "ultraresearch_frontier")]
    [Description(
        "Get the current research frontier: papers queued for fetching, " +
        "ordered by priority. Shows what the agent plans to investigate next " +
        "with IDs, types, source, priority scores, and citation counts.")]
    public static Task<string> GetFrontierAsync(
        [Description("Session ID returned by ultraresearch_start")]
        string sessionId,
        [Description("Max candidates to return (default: 20)")]
        int limit = 20)
    {
        try
        {
            if (CheckConfigured() is { } err) return Task.FromResult(err);

            if (!Guid.TryParse(sessionId, out var id))
                return Task.FromResult(Json(new { success = false, error = "Invalid session ID format" }));

            limit = Math.Clamp(limit, 1, 100);

            var candidates = _orchestrator!.GetFrontierSnapshot(id, limit);
            if (candidates == null)
                return Task.FromResult(Json(new { success = false, error = $"Session {sessionId} not found" }));

            var status = _orchestrator.GetStatus(id);

            return Task.FromResult(Json(new
            {
                success = true,
                session_id = id,
                topic = status?.Topic,
                frontier_total = status?.FrontierSize ?? candidates.Count,
                returned = candidates.Count,
                candidates = candidates.Select((c, idx) => new
                {
                    rank = idx + 1,
                    id = c.Id,
                    type = c.Type,
                    source = c.Source.ToString(),
                    priority = Math.Round(c.Priority, 3),
                    cited_by = c.CitedByCount,
                    title = c.Title,
                    discovered_from = c.DiscoveredFrom
                })
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Json(new { success = false, error = ex.Message }));
        }
    }

    [McpServerTool(Name = "ultraresearch_checkpoints")]
    [Description(
        "Get sentinel checkpoint history for a research session. " +
        "Checkpoints contain convergence metrics (newInfoRatio), identified knowledge gaps, " +
        "suggested queries, and whether the sentinel recommends continuing.")]
    public static Task<string> GetCheckpointsAsync(
        [Description("Session ID returned by ultraresearch_start")]
        string sessionId,
        [Description("Max checkpoints to return, most recent first (default: 5)")]
        int limit = 5)
    {
        try
        {
            if (CheckConfigured() is { } err) return Task.FromResult(err);

            if (!Guid.TryParse(sessionId, out var id))
                return Task.FromResult(Json(new { success = false, error = "Invalid session ID format" }));

            limit = Math.Clamp(limit, 1, 50);

            var checkpoints = _orchestrator!.GetCheckpointsSnapshot(id, limit);
            if (checkpoints == null)
                return Task.FromResult(Json(new { success = false, error = $"Session {sessionId} not found" }));

            var status = _orchestrator.GetStatus(id);

            return Task.FromResult(Json(new
            {
                success = true,
                session_id = id,
                topic = status?.Topic,
                checkpoint_total = status?.CheckpointCount ?? checkpoints.Count,
                returned = checkpoints.Count,
                checkpoints = checkpoints.Select(cp => new
                {
                    iteration = cp.Iteration,
                    total_papers = cp.TotalPapers,
                    total_entities = cp.TotalEntities,
                    orphan_citations = cp.OrphanCitations,
                    new_info_ratio = Math.Round(cp.NewInfoRatio, 3),
                    should_continue = cp.ShouldContinue,
                    identified_gaps = cp.IdentifiedGaps,
                    suggested_queries = cp.SuggestedQueries,
                    sentinel_analysis = cp.SentinelAnalysis,
                    timestamp = cp.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ")
                })
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Json(new { success = false, error = ex.Message }));
        }
    }

    [McpServerTool(Name = "ultraresearch_stats")]
    [Description(
        "Get aggregate statistics for an UltraResearch session: " +
        "paper counts, success/failure rates, convergence metrics, " +
        "and timing information.")]
    public static Task<string> GetSessionStatsAsync(
        [Description("Session ID returned by ultraresearch_start")]
        string sessionId)
    {
        try
        {
            if (CheckConfigured() is { } err) return Task.FromResult(err);

            if (!Guid.TryParse(sessionId, out var id))
                return Task.FromResult(Json(new { success = false, error = "Invalid session ID format" }));

            var status = _orchestrator!.GetStatus(id);
            if (status == null)
                return Task.FromResult(Json(new { success = false, error = $"Session {sessionId} not found" }));

            var elapsed = (status.CompletedAt ?? DateTimeOffset.UtcNow) - status.StartedAt;
            var successRate = status.PapersFetched > 0
                ? (double)status.PapersIngested / status.PapersFetched
                : 0.0;

            return Task.FromResult(Json(new
            {
                success = true,
                session_id = id,
                topic = status.Topic,
                status = status.Status.ToString(),
                timing = new
                {
                    started_at = status.StartedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    completed_at = status.CompletedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    elapsed_minutes = Math.Round(elapsed.TotalMinutes, 1)
                },
                papers = new
                {
                    fetched = status.PapersFetched,
                    ingested = status.PapersIngested,
                    failed = status.PapersFailed,
                    success_rate = Math.Round(successRate, 3)
                },
                exploration = new
                {
                    iterations = status.Iteration,
                    frontier_size = status.FrontierSize,
                    seen_ids = status.SeenIdsCount,
                    checkpoints = status.CheckpointCount
                },
                stop_reason = status.StopReason
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Json(new { success = false, error = ex.Message }));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static string Json(object obj) => JsonSerializer.Serialize(obj, JsonOpts);

    /// <summary>No-op ingester for dry-run mode.</summary>
    private sealed class NoOpIngester : IDocumentIngester
    {
        public Task<DocumentIngestResult> IngestAsync(string filePath, Guid collectionId, CancellationToken ct = default)
            => Task.FromResult(new DocumentIngestResult(true, "Dry-run: skipped ingestion"));
    }
}

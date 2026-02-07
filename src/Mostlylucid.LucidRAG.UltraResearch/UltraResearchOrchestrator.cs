using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LucidRAG.UltraResearch;

/// <summary>
///     Main agentic loop for autonomous research corpus building.
///     Manages session lifecycle, state persistence, convergence detection,
///     and Channel-based progress streaming.
/// </summary>
public class UltraResearchOrchestrator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<UltraResearchOrchestrator> _logger;

    private readonly ConcurrentDictionary<Guid, ActiveSession> _activeSessions = new();

    public UltraResearchOrchestrator(
        IServiceProvider services,
        ILogger<UltraResearchOrchestrator> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    ///     Start a new UltraResearch session. Returns the session ID immediately;
    ///     the loop runs in the background via Task.Run.
    /// </summary>
    public async Task<Guid> StartAsync(
        UltraResearchConfig config,
        IDocumentIngester ingester,
        CancellationToken ct = default)
    {
        config.Validate();

        Guid collectionId;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            var collectionName = config.CollectionName ?? GenerateCollectionName(config.Topic);

            var collection = await db.Collections.FirstOrDefaultAsync(c => c.Name == collectionName, ct);
            if (collection == null)
            {
                collection = new CollectionEntity
                {
                    Id = Guid.NewGuid(),
                    Name = collectionName,
                    Description = $"UltraResearch corpus: {config.Topic}"
                };
                db.Collections.Add(collection);
                await db.SaveChangesAsync(ct);
            }

            collectionId = collection.Id;
        }

        var state = new UltraResearchState
        {
            CollectionId = collectionId,
            Topic = config.Topic,
            Config = config
        };

        // Seed frontier
        foreach (var arxivId in config.SeedArxivIds)
        {
            var key = ResearchPaperFetcher.NormalizeSeenKey("arxiv", arxivId);
            if (state.SeenIds.Add(key))
            {
                state.Frontier.Add(new FetchCandidate
                {
                    Id = arxivId,
                    Type = "arxiv",
                    Source = CandidateSource.Search,
                    Priority = 1.0,
                    Title = null
                });
            }
        }

        foreach (var doi in config.SeedDois)
        {
            var key = ResearchPaperFetcher.NormalizeSeenKey("doi", doi);
            if (state.SeenIds.Add(key))
            {
                state.Frontier.Add(new FetchCandidate
                {
                    Id = doi,
                    Type = "doi",
                    Source = CandidateSource.Search,
                    Priority = 1.0,
                    Title = null
                });
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressChannel = Channel.CreateBounded<UltraResearchProgress>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

        var session = new ActiveSession(state, cts, progressChannel);
        _activeSessions[state.SessionId] = session;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(config, state, ingester, session, cts.Token);
            }
            finally
            {
                CleanupSession(state.SessionId);
            }
        }, cts.Token);

        _logger.LogInformation("UltraResearch session {SessionId} started for '{Topic}' in collection {CollectionId}",
            state.SessionId, config.Topic, collectionId);

        return state.SessionId;
    }

    /// <summary>
    ///     Resume a previously persisted session from CollectionEntity.Settings.
    /// </summary>
    public async Task<Guid?> ResumeAsync(
        Guid collectionId,
        IDocumentIngester ingester,
        CancellationToken ct = default)
    {
        UltraResearchState? state;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
            var collection = await db.Collections.FindAsync([collectionId], ct);
            if (collection?.Settings == null) return null;

            try
            {
                state = JsonSerializer.Deserialize<UltraResearchState>(collection.Settings);
            }
            catch
            {
                _logger.LogWarning("Failed to deserialize UltraResearch state for collection {CollectionId}", collectionId);
                return null;
            }

            if (state == null || state.Status != UltraResearchStatus.Running) return null;
        }

        // Restore case-insensitive comparers lost during JSON round-trip
        state.RestoreComparers();

        // Restore config from persisted state, or fall back to defaults with topic
        var config = state.Config ?? new UltraResearchConfig { Topic = state.Topic };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressChannel = Channel.CreateBounded<UltraResearchProgress>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

        var session = new ActiveSession(state, cts, progressChannel);
        _activeSessions[state.SessionId] = session;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(config, state, ingester, session, cts.Token);
            }
            finally
            {
                CleanupSession(state.SessionId);
            }
        }, cts.Token);

        _logger.LogInformation("Resumed UltraResearch session {SessionId} at iteration {Iteration}",
            state.SessionId, state.Iteration);

        return state.SessionId;
    }

    /// <summary>Gracefully stop a running session.</summary>
    public void Stop(Guid sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.Cts.Cancel();
            _logger.LogInformation("Requested stop for session {SessionId}", sessionId);
        }
    }

    /// <summary>Get a thread-safe snapshot of the current session state.</summary>
    public UltraResearchStateSnapshot? GetStatus(Guid sessionId)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session)) return null;
        var s = session.State;
        return new UltraResearchStateSnapshot(
            s.SessionId, s.CollectionId, s.Status, s.Topic,
            s.Iteration, s.PapersFetched, s.PapersIngested, s.PapersFailed,
            s.Frontier.Count, s.SeenIds.Count, s.Checkpoints.Count,
            s.StartedAt, s.CompletedAt, s.StopReason);
    }

    /// <summary>Stream progress updates from a running session.</summary>
    public IAsyncEnumerable<UltraResearchProgress>? StreamProgress(Guid sessionId)
    {
        return _activeSessions.TryGetValue(sessionId, out var session)
            ? session.ProgressChannel.Reader.ReadAllAsync()
            : null;
    }

    private async Task RunLoopAsync(
        UltraResearchConfig config,
        UltraResearchState state,
        IDocumentIngester ingester,
        ActiveSession session,
        CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var fetcher = scope.ServiceProvider.GetRequiredService<ResearchPaperFetcher>();
            var frontier = scope.ServiceProvider.GetRequiredService<ResearchFrontierManager>();
            var sentinel = scope.ServiceProvider.GetRequiredService<ResearchSentinelEvaluator>();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            var dataDir = config.DataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lucidrag");
            var startTime = DateTimeOffset.UtcNow;
            var papersSinceLastSentinel = 0;
            var consecutiveLowInfo = 0;

            // Phase 1: Initial search
            EmitProgress(session, ResearchStage.Searching,
                $"Searching for '{config.Topic}'...", state);

            var initialCandidates = await fetcher.SearchAsync(config.Topic, config, state.SeenIds, ct);
            frontier.AddDiscoveredCandidates(initialCandidates, state);
            state.SearchQueriesUsed.Add(config.Topic);

            // Phase 2: Main agentic loop
            while (!ct.IsCancellationRequested)
            {
                state.Iteration++;

                // Check budget
                if (state.PapersFetched >= config.MaxPapers)
                {
                    state.StopReason = $"Budget exhausted: {state.PapersFetched}/{config.MaxPapers} papers";
                    break;
                }

                if (DateTimeOffset.UtcNow - startTime > config.MaxDuration)
                {
                    state.StopReason = $"Duration limit: {config.MaxDuration}";
                    break;
                }

                if (state.Iteration > config.MaxIterations)
                {
                    state.StopReason = $"Max iterations: {config.MaxIterations}";
                    break;
                }

                // 2a. SELECT
                var batch = frontier.GetNextBatch(state, config.BatchSize);
                if (batch.Count == 0)
                {
                    // Try sentinel queries before giving up
                    var lastCheckpoint = state.Checkpoints.LastOrDefault();
                    if (lastCheckpoint?.SuggestedQueries.Count > 0)
                    {
                        foreach (var query in lastCheckpoint.SuggestedQueries)
                        {
                            if (state.SearchQueriesUsed.Add(query))
                            {
                                var results = await fetcher.SearchAsync(query, config, state.SeenIds, ct);
                                frontier.AddDiscoveredCandidates(results, state);
                            }
                        }

                        batch = frontier.GetNextBatch(state, config.BatchSize);
                    }

                    if (batch.Count == 0)
                    {
                        // Try topic variations
                        var variations = GenerateTopicVariations(config.Topic, state);
                        foreach (var v in variations)
                        {
                            if (state.SearchQueriesUsed.Add(v))
                            {
                                var results = await fetcher.SearchAsync(v, config, state.SeenIds, ct);
                                frontier.AddDiscoveredCandidates(results, state);
                            }
                        }

                        batch = frontier.GetNextBatch(state, config.BatchSize);
                    }

                    if (batch.Count == 0)
                    {
                        state.StopReason = "Frontier exhausted: no more candidates";
                        break;
                    }
                }

                // 2b. FETCH + INGEST
                EmitProgress(session, ResearchStage.Fetching,
                    $"Fetching batch of {batch.Count} papers...", state);

                foreach (var candidate in batch)
                {
                    if (ct.IsCancellationRequested) break;

                    var fetched = await fetcher.FetchAndPrepareAsync(candidate, dataDir, ct);
                    if (fetched == null)
                    {
                        state.PapersFailed++;
                        continue;
                    }

                    state.PapersFetched++;

                    // Add discovered citations to frontier
                    var newCandidates = new List<FetchCandidate>();
                    foreach (var (citType, citId) in fetched.CitationIds)
                    {
                        var key = ResearchPaperFetcher.NormalizeSeenKey(citType, citId);
                        if (!state.SeenIds.Add(key)) continue;

                        newCandidates.Add(new FetchCandidate
                        {
                            Id = citId,
                            Type = citType,
                            Source = CandidateSource.Citation,
                            DiscoveredFrom = candidate.Id,
                            Title = null
                        });
                    }
                    frontier.AddDiscoveredCandidates(newCandidates, state);

                    // Get reverse citations from S2
                    if (config.IncludeSemanticScholar)
                    {
                        var reverseCitations = await fetcher.GetReverseCitationsAsync(candidate, state.SeenIds, ct);
                        frontier.AddDiscoveredCandidates(reverseCitations, state);
                    }

                    // Ingest into pipeline
                    if (!config.DryRun)
                    {
                        var titleSnippet = fetched.Title.Length > 60 ? fetched.Title[..60] : fetched.Title;
                        EmitProgress(session, ResearchStage.Ingesting,
                            $"Ingesting: {titleSnippet}...", state);

                        var ingestResult = await ingester.IngestAsync(fetched.FilePath, state.CollectionId, ct);
                        if (ingestResult.Success)
                        {
                            state.PapersIngested++;
                            papersSinceLastSentinel++;
                        }
                        else
                        {
                            _logger.LogWarning("Ingestion failed for {File}: {Message}",
                                fetched.FilePath, ingestResult.Message);
                        }
                    }
                    else
                    {
                        state.PapersIngested++;
                        papersSinceLastSentinel++;
                    }
                }

                // 2c. ANALYZE
                if (!config.DryRun)
                {
                    EmitProgress(session, ResearchStage.Analyzing,
                        "Analyzing citation graph...", state);
                    await frontier.RefreshFrontierAsync(state, state.CollectionId, ct);
                }

                // 2d. SENTINEL
                if (papersSinceLastSentinel >= config.SentinelInterval)
                {
                    EmitProgress(session, ResearchStage.Sentinel,
                        "Running sentinel evaluation...", state);

                    var checkpoint = await sentinel.EvaluateAsync(state, state.CollectionId, ct);
                    state.Checkpoints.Add(checkpoint);
                    papersSinceLastSentinel = 0;

                    // Add sentinel queries to frontier
                    foreach (var query in checkpoint.SuggestedQueries)
                    {
                        if (state.SearchQueriesUsed.Add(query))
                        {
                            var results = await fetcher.SearchAsync(query, config, state.SeenIds, ct);
                            frontier.AddDiscoveredCandidates(results, state);
                        }
                    }

                    // Check convergence
                    if (checkpoint.NewInfoRatio < config.ConvergenceThreshold)
                        consecutiveLowInfo++;
                    else
                        consecutiveLowInfo = 0;

                    if (consecutiveLowInfo >= 3)
                    {
                        state.StopReason = $"Converged: newInfoRatio < {config.ConvergenceThreshold} for 3 consecutive checkpoints";
                        break;
                    }

                    if (!checkpoint.ShouldContinue && state.Frontier.Count == 0)
                    {
                        state.StopReason = "Sentinel determined research is complete";
                        break;
                    }
                }

                // 2e. PERSIST
                PersistState(db, state);

                EmitProgress(session, ResearchStage.Searching,
                    $"Iteration {state.Iteration} complete. Frontier: {state.Frontier.Count} candidates", state);
            }

            // Finalize
            state.Status = ct.IsCancellationRequested ? UltraResearchStatus.Stopped : UltraResearchStatus.Completed;
            state.CompletedAt = DateTimeOffset.UtcNow;
            state.StopReason ??= "Completed normally";

            PersistState(db, state);

            EmitProgress(session, ResearchStage.Finalizing,
                $"Session complete: {state.PapersIngested} papers ingested. {state.StopReason}", state);

            _logger.LogInformation(
                "UltraResearch session {SessionId} finished: {Ingested} papers, {Iterations} iterations, reason: {Reason}",
                state.SessionId, state.PapersIngested, state.Iteration, state.StopReason);
        }
        catch (OperationCanceledException)
        {
            state.Status = UltraResearchStatus.Stopped;
            state.StopReason = "Cancelled by user";
            state.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Session {SessionId} cancelled", state.SessionId);
        }
        catch (Exception ex)
        {
            state.Status = UltraResearchStatus.Failed;
            state.StopReason = $"Error: {ex.Message}";
            state.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogError(ex, "Session {SessionId} failed", state.SessionId);
        }
        finally
        {
            session.ProgressChannel.Writer.TryComplete();
        }
    }

    private void PersistState(RagDocumentsDbContext db, UltraResearchState state)
    {
        try
        {
            var collection = db.Collections.Find(state.CollectionId);
            if (collection != null)
            {
                collection.Settings = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = false
                });
                collection.UpdatedAt = DateTimeOffset.UtcNow;
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist state for collection {CollectionId}", state.CollectionId);
        }
    }

    private void CleanupSession(Guid sessionId)
    {
        if (_activeSessions.TryRemove(sessionId, out var session))
        {
            session.Cts.Dispose();
        }
    }

    private static void EmitProgress(ActiveSession session, ResearchStage stage, string message, UltraResearchState state)
    {
        var progress = new UltraResearchProgress(
            stage, message, state.Iteration,
            state.PapersFetched, state.PapersIngested,
            state.Frontier.Count,
            state.Checkpoints.LastOrDefault()?.NewInfoRatio);

        // TryWrite: non-blocking, won't throw on completed channel. DropOldest handles backpressure.
        session.ProgressChannel.Writer.TryWrite(progress);
    }

    private static List<string> GenerateTopicVariations(string topic, UltraResearchState state)
    {
        var variations = new List<string>();
        var words = topic.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        variations.Add($"{topic} survey");
        variations.Add($"{topic} review");

        if (words.Length >= 4)
            variations.Add(string.Join(" ", words.Take(words.Length / 2)));

        return variations.Where(v => !state.SearchQueriesUsed.Contains(v)).Take(3).ToList();
    }

    /// <summary>Generate a standardized collection name from a research topic.</summary>
    public static string GenerateCollectionName(string topic)
    {
        var slug = topic.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("\"", "")
            .Replace("'", "");

        if (slug.Length > 40) slug = slug[..40];
        slug = slug.TrimEnd('-');

        return $"ultraresearch-{slug}-{DateTimeOffset.UtcNow:yyyyMMdd}";
    }

    internal record ActiveSession(
        UltraResearchState State,
        CancellationTokenSource Cts,
        Channel<UltraResearchProgress> ProgressChannel);
}

/// <summary>
///     Thread-safe snapshot of session state exposed via <see cref="UltraResearchOrchestrator.GetStatus"/>.
///     Captures counters at a point in time without exposing mutable collections.
/// </summary>
public record UltraResearchStateSnapshot(
    Guid SessionId,
    Guid CollectionId,
    UltraResearchStatus Status,
    string Topic,
    int Iteration,
    int PapersFetched,
    int PapersIngested,
    int PapersFailed,
    int FrontierSize,
    int SeenIdsCount,
    int CheckpointCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? StopReason);

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.UltraResearch.Signals;
using LucidRAG.UltraResearch.Waves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

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

    /// <summary>
    ///     Retains final status snapshots for completed/stopped/failed sessions so callers
    ///     can inspect results after the session has been cleaned up. Bounded to 100 entries.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, UltraResearchStateSnapshot> _completedSessions = new();
    private readonly ConcurrentDictionary<Guid, ResearchSynthesis?> _completedSyntheses = new();
    private readonly Queue<Guid> _completedSessionOrder = new();
    private readonly object _completedSessionLock = new();
    private const int MaxCompletedSessionsRetained = 100;

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

    /// <summary>
    ///     Get a thread-safe snapshot of session state. Returns data for both active
    ///     and recently completed sessions (retained up to <see cref="MaxCompletedSessionsRetained"/>).
    /// </summary>
    public UltraResearchStateSnapshot? GetStatus(Guid sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            var s = session.State;
            return new UltraResearchStateSnapshot(
                s.SessionId, s.CollectionId, s.Status, s.Topic,
                s.Iteration, s.PapersFetched, s.PapersIngested, s.PapersFailed,
                s.Frontier.Count, s.SeenIds.Count, s.Checkpoints.Count,
                s.StartedAt, s.CompletedAt, s.StopReason);
        }

        // Fall back to completed sessions cache
        return _completedSessions.TryGetValue(sessionId, out var completed) ? completed : null;
    }

    /// <summary>Stream progress updates from a running session.</summary>
    public IAsyncEnumerable<UltraResearchProgress>? StreamProgress(Guid sessionId)
    {
        return _activeSessions.TryGetValue(sessionId, out var session)
            ? session.ProgressChannel.Reader.ReadAllAsync()
            : null;
    }

    /// <summary>Get a snapshot of the current frontier candidates, ordered by priority.</summary>
    public List<FetchCandidate>? GetFrontierSnapshot(Guid sessionId, int limit = 20)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session)) return null;
        // Take a snapshot — the list may be modified concurrently by the loop
        try
        {
            return session.State.Frontier
                .OrderByDescending(c => c.Priority)
                .Take(limit)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // Collection modified during enumeration — return empty rather than throw
            return [];
        }
    }

    /// <summary>Get a snapshot of sentinel checkpoints, most recent first.</summary>
    public List<SentinelCheckpoint>? GetCheckpointsSnapshot(Guid sessionId, int limit = 5)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session)) return null;
        try
        {
            return session.State.Checkpoints
                .AsEnumerable()
                .Reverse()
                .Take(limit)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Get the synthesis result for a session (active or completed).</summary>
    public ResearchSynthesis? GetSynthesis(Guid sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
            return session.State.Synthesis;
        _completedSyntheses.TryGetValue(sessionId, out var synthesis);
        return synthesis;
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

            // Initialize session-level analysis context for cross-paper signal accumulation
            state.SessionContext ??= new AnalysisContext();

            // Resolve PaperCoordinator if available (wave-coordinated processing)
            var paperCoordinator = scope.ServiceProvider.GetService<PaperCoordinator>();

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

                // 2b. FETCH + INGEST (wave-coordinated if PaperCoordinator available, else legacy)
                EmitProgress(session, ResearchStage.Fetching,
                    $"Fetching batch of {batch.Count} papers...", state);

                if (paperCoordinator != null)
                {
                    // Wave-coordinated: process papers concurrently through PaperCoordinator
                    // The io lane semaphore naturally rate-limits API calls
                    // Track counters via Interlocked — wave tasks run concurrently
                    var papersFailed = 0;
                    var papersFetched = 0;
                    var papersIngested = 0;
                    var papersSinceLastSentinelCount = 0;

                    var paperTasks = batch.Select(async candidate =>
                    {
                        if (ct.IsCancellationRequested) return;

                        try
                        {
                            var result = await paperCoordinator.ProcessAsync(
                                candidate, state.SessionContext, ingester,
                                state.CollectionId, dataDir, config.DryRun, ct);

                            // Extract results from signals
                            var fetchSuccess = result.Context.GetValue<bool>(ResearchSignalKeys.PaperFetchSuccess);
                            if (!fetchSuccess)
                            {
                                Interlocked.Increment(ref papersFailed);
                                return;
                            }

                            Interlocked.Increment(ref papersFetched);

                            // Add discovered citations to frontier
                            var citationIds = result.Context.GetCached<List<(string type, string id)>>("citation_ids");
                            if (citationIds != null)
                            {
                                var newCandidates = new List<FetchCandidate>();
                                foreach (var (citType, citId) in citationIds)
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
                            }

                            // Get reverse citations from S2
                            if (config.IncludeSemanticScholar)
                            {
                                var reverseCitations = await fetcher.GetReverseCitationsAsync(candidate, state.SeenIds, ct);
                                frontier.AddDiscoveredCandidates(reverseCitations, state);
                            }

                            var ingestSuccess = result.Context.GetValue<bool>(ResearchSignalKeys.PaperIngestSuccess);
                            if (ingestSuccess || config.DryRun)
                            {
                                Interlocked.Increment(ref papersIngested);
                                Interlocked.Increment(ref papersSinceLastSentinelCount);
                            }

                            var title = result.Context.GetValue<string>(ResearchSignalKeys.PaperMetadataTitle);
                            if (!string.IsNullOrEmpty(title))
                            {
                                var titleSnippet = title.Length > 60 ? title[..60] : title;
                                EmitProgress(session, ResearchStage.Ingesting, $"Processed: {titleSnippet}", state);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Wave processing failed for {Type}:{Id}", candidate.Type, candidate.Id);
                            Interlocked.Increment(ref papersFailed);
                        }
                    });

                    await Task.WhenAll(paperTasks);

                    state.PapersFailed += papersFailed;
                    state.PapersFetched += papersFetched;
                    state.PapersIngested += papersIngested;
                    papersSinceLastSentinel += papersSinceLastSentinelCount;
                }
                else
                {
                    // Legacy sequential processing (fallback when PaperCoordinator not registered)
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

                    // Use signal-based evaluation when session context is available
                    var checkpoint = state.SessionContext != null
                        ? await sentinel.EvaluateFromSignalsAsync(state, state.SessionContext, state.CollectionId, ct)
                        : await sentinel.EvaluateAsync(state, state.CollectionId, ct);
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

                    // Signal-based aggressive early exit: if new info ratio drops below half
                    // the convergence threshold, exit immediately
                    if (state.SessionContext != null)
                    {
                        var ratio = state.SessionContext.GetValue<double>(ResearchSignalKeys.SessionConvergenceNewInfoRatio);
                        if (ratio < config.ConvergenceThreshold * 0.5 && state.PapersIngested >= config.SentinelInterval * 2)
                        {
                            state.StopReason = $"Early exit: newInfoRatio {ratio:F3} < {config.ConvergenceThreshold * 0.5:F3} (aggressive convergence)";
                            break;
                        }
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

            // Synthesis step — generate a summary of the research corpus
            // Generate for both Completed and Stopped (user-stopped) sessions
            if (state.PapersIngested > 0)
            {
                await GenerateSynthesisAsync(scope.ServiceProvider, state, dataDir, session);
            }

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

            // Generate synthesis even for cancelled sessions (fresh scope — original is disposed)
            if (state.PapersIngested > 0)
            {
                var dataDir = state.Config?.DataDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lucidrag");
                using var synthScope = _services.CreateScope();
                await GenerateSynthesisAsync(synthScope.ServiceProvider, state, dataDir, session);
                PersistState(synthScope.ServiceProvider, state);
            }
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

    /// <summary>Persist state using a fresh service provider (for cancelled sessions where original scope is disposed).</summary>
    private void PersistState(IServiceProvider services, UltraResearchState state)
    {
        try
        {
            var db = services.GetRequiredService<RagDocumentsDbContext>();
            PersistState(db, state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist state for collection {CollectionId}", state.CollectionId);
        }
    }

    /// <summary>
    ///     Generate synthesis from the research corpus. Works for both completed and stopped sessions.
    ///     Uses CancellationToken.None to ensure synthesis completes even after user cancellation.
    /// </summary>
    private async Task GenerateSynthesisAsync(
        IServiceProvider services,
        UltraResearchState state,
        string dataDir,
        ActiveSession session)
    {
        EmitProgress(session, ResearchStage.Synthesizing, "Generating research synthesis...", state);
        try
        {
            var synthesizer = services.GetRequiredService<ResearchSynthesizer>();
            state.Synthesis = await synthesizer.SynthesizeAsync(state, state.CollectionId, CancellationToken.None);

            var filename = $"synthesis-{SanitizeFilename(state.Topic)}-{state.CompletedAt:yyyyMMdd-HHmmss}.md";
            var filePath = Path.Combine(dataDir, filename);
            await File.WriteAllTextAsync(filePath, state.Synthesis.SynthesisMarkdown, CancellationToken.None);
            state.Synthesis.SavedFilePath = filePath;

            _logger.LogInformation("Synthesis saved to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate synthesis (non-fatal)");
        }
    }

    private void CleanupSession(Guid sessionId)
    {
        if (_activeSessions.TryRemove(sessionId, out var session))
        {
            // Retain a final snapshot so callers can inspect completed session results
            var s = session.State;
            var snapshot = new UltraResearchStateSnapshot(
                s.SessionId, s.CollectionId, s.Status, s.Topic,
                s.Iteration, s.PapersFetched, s.PapersIngested, s.PapersFailed,
                s.Frontier.Count, s.SeenIds.Count, s.Checkpoints.Count,
                s.StartedAt, s.CompletedAt, s.StopReason);
            _completedSessions[sessionId] = snapshot;
            _completedSyntheses[sessionId] = s.Synthesis;

            // O(1) eviction via insertion-order queue instead of O(n log n) sort
            lock (_completedSessionLock)
            {
                _completedSessionOrder.Enqueue(sessionId);
                while (_completedSessions.Count > MaxCompletedSessionsRetained &&
                       _completedSessionOrder.Count > 0)
                {
                    var toEvict = _completedSessionOrder.Dequeue();
                    _completedSessions.TryRemove(toEvict, out _);
                    _completedSyntheses.TryRemove(toEvict, out _);
                }
            }

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

    private static string SanitizeFilename(string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        safe = safe.Replace(' ', '-');
        return safe.Length > 40 ? safe[..40] : safe;
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

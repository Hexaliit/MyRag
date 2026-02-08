using LucidRAG.UltraResearch;
using Microsoft.Extensions.Logging;

namespace LucidResearch.Services;

/// <summary>
///     Polls the UltraResearchOrchestrator every 500ms and updates AppState reactive properties.
/// </summary>
public class StatePoller
{
    private readonly UltraResearchOrchestrator _orchestrator;
    private readonly AppState _appState;
    private readonly ILogger<StatePoller> _logger;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    public StatePoller(
        UltraResearchOrchestrator orchestrator,
        AppState appState,
        ILogger<StatePoller> logger)
    {
        _orchestrator = orchestrator;
        _appState = appState;
        _logger = logger;
    }

    public void Start()
    {
        if (_pollTask != null) return;
        _cts = new CancellationTokenSource();
        // LongRunning avoids ThreadPool contention for a dedicated polling loop
        _pollTask = Task.Factory.StartNew(
            () => PollLoopAsync(_cts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        await _cts.CancelAsync();
        if (_pollTask != null)
            await _pollTask;
        _cts.Dispose();
        _cts = null;
        _pollTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_appState.ActiveSessionId.Value is { } sid)
                {
                    var snapshot = _orchestrator.GetStatus(sid);
                    if (snapshot != null)
                    {
                        _appState.Status.Value = snapshot.Status;
                        _appState.Topic.Value = snapshot.Topic;
                        _appState.Iteration.Value = snapshot.Iteration;
                        _appState.PapersFetched.Value = snapshot.PapersFetched;
                        _appState.PapersIngested.Value = snapshot.PapersIngested;
                        _appState.PapersFailed.Value = snapshot.PapersFailed;
                        _appState.FrontierSize.Value = snapshot.FrontierSize;
                        _appState.SeenCount.Value = snapshot.SeenIdsCount;

                        // Single checkpoint fetch for both latest metrics and convergence history
                        var checkpoints = _orchestrator.GetCheckpointsSnapshot(sid, 50);
                        if (checkpoints is { Count: > 0 })
                        {
                            var latest = checkpoints[0]; // Most recent first
                            _appState.NewInfoRatio.Value = latest.NewInfoRatio;
                            _appState.TotalEntities.Value = latest.TotalEntities;
                            _appState.OrphanCitations.Value = latest.OrphanCitations;

                            // Build history list without re-sorting — checkpoints come
                            // in reverse order, so iterate backwards to get ascending
                            var history = new List<double>(checkpoints.Count);
                            for (var i = checkpoints.Count - 1; i >= 0; i--)
                                history.Add(checkpoints[i].NewInfoRatio);
                            _appState.NewInfoHistory.Value = history;
                        }

                        // If session completed, add activity
                        if (snapshot.Status != UltraResearchStatus.Running &&
                            snapshot.StopReason != null)
                        {
                            _appState.AddActivity($"Session ended: {snapshot.StopReason}");
                        }
                    }
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling orchestrator state");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }
}

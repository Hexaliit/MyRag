using LucidRAG.UltraResearch;
using Microsoft.Extensions.Logging;
using XenoAtom.Terminal.UI.Threading;

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
                // Read ActiveSessionId on the UI thread
                var sid = await Dispatcher.Current.InvokeAsync(() => _appState.ActiveSessionId.Value);

                if (sid is { } sessionId)
                {
                    // Fetch data from orchestrator on background thread (no UI access needed)
                    var snapshot = _orchestrator.GetStatus(sessionId);
                    if (snapshot != null)
                    {
                        var checkpoints = _orchestrator.GetCheckpointsSnapshot(sessionId, 50);

                        // Marshal all State updates to the UI thread
                        Dispatcher.Current.Post(() =>
                        {
                            _appState.Status.Value = snapshot.Status;
                            _appState.Topic.Value = snapshot.Topic;
                            _appState.Iteration.Value = snapshot.Iteration;
                            _appState.PapersFetched.Value = snapshot.PapersFetched;
                            _appState.PapersIngested.Value = snapshot.PapersIngested;
                            _appState.PapersFailed.Value = snapshot.PapersFailed;
                            _appState.FrontierSize.Value = snapshot.FrontierSize;
                            _appState.SeenCount.Value = snapshot.SeenIdsCount;

                            if (checkpoints is { Count: > 0 })
                            {
                                var latest = checkpoints[0];
                                _appState.NewInfoRatio.Value = latest.NewInfoRatio;
                                _appState.TotalEntities.Value = latest.TotalEntities;
                                _appState.OrphanCitations.Value = latest.OrphanCitations;

                                var history = new List<double>(checkpoints.Count);
                                for (var i = checkpoints.Count - 1; i >= 0; i--)
                                    history.Add(checkpoints[i].NewInfoRatio);
                                _appState.NewInfoHistory.Value = history;
                            }

                            if (snapshot.Status != UltraResearchStatus.Running &&
                                snapshot.StopReason != null)
                            {
                                _appState.AddActivity($"Session ended: {snapshot.StopReason}");
                            }

                            // Check for synthesis after status update
                            if (snapshot.Status is UltraResearchStatus.Completed or UltraResearchStatus.Stopped)
                            {
                                var synthesis = _orchestrator.GetSynthesis(sessionId);
                                if (synthesis != null && string.IsNullOrEmpty(_appState.SynthesisText.Value))
                                {
                                    _appState.SynthesisText.Value = synthesis.SynthesisMarkdown;
                                    _appState.SynthesisFilePath.Value = synthesis.SavedFilePath;
                                    _appState.AddActivity("Research synthesis ready — press F6 to view");
                                    if (snapshot.Status == UltraResearchStatus.Completed)
                                        _appState.CurrentView.Value = ViewMode.Summary;
                                }
                            }
                        });
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

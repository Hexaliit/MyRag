using LucidRAG.UltraResearch;
using XenoAtom.Terminal.UI;

namespace LucidResearch;

public class AppState
{
    // Quick-start from CLI argument
    public State<string?> PendingQuickStart { get; } = new(null);

    // Active session tracking
    public State<Guid?> ActiveSessionId { get; } = new(null);
    public State<string> Topic { get; } = new("");
    public State<UltraResearchStatus> Status { get; } = new(UltraResearchStatus.Completed);

    // Live metrics (polled from orchestrator)
    public State<int> Iteration { get; } = new(0);
    public State<int> PapersFetched { get; } = new(0);
    public State<int> PapersIngested { get; } = new(0);
    public State<int> PapersFailed { get; } = new(0);
    public State<int> FrontierSize { get; } = new(0);
    public State<int> SeenCount { get; } = new(0);

    // Sentinel metrics
    public State<double> NewInfoRatio { get; } = new(1.0);
    public State<int> TotalEntities { get; } = new(0);
    public State<int> OrphanCitations { get; } = new(0);

    // Convergence history (for chart)
    public State<List<double>> NewInfoHistory { get; } = new([]);

    // Synthesis
    public State<string?> SynthesisText { get; } = new(null);
    public State<string?> SynthesisFilePath { get; } = new(null);

    // Navigation
    public State<ViewMode> CurrentView { get; } = new(ViewMode.Dashboard);
    public State<bool> ExitRequested { get; } = new(false);

    // Activity log — backed by a Queue for O(1) enqueue/dequeue
    public State<List<string>> ActivityLog { get; } = new([]);
    private readonly Queue<string> _activityQueue = new();
    private const int MaxActivityEntries = 100;

    public void AddActivity(string message)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss");
        _activityQueue.Enqueue($"[{timestamp}] {message}");
        while (_activityQueue.Count > MaxActivityEntries)
            _activityQueue.Dequeue();
        ActivityLog.Value = new List<string>(_activityQueue);
    }

    public void Reset()
    {
        ActiveSessionId.Value = null;
        Topic.Value = "";
        Status.Value = UltraResearchStatus.Completed;
        Iteration.Value = 0;
        PapersFetched.Value = 0;
        PapersIngested.Value = 0;
        PapersFailed.Value = 0;
        FrontierSize.Value = 0;
        SeenCount.Value = 0;
        NewInfoRatio.Value = 1.0;
        TotalEntities.Value = 0;
        OrphanCitations.Value = 0;
        NewInfoHistory.Value = [];
        SynthesisText.Value = null;
        SynthesisFilePath.Value = null;
        _activityQueue.Clear();
        ActivityLog.Value = [];
    }
}

public enum ViewMode
{
    Dashboard,
    StartResearch,
    Frontier,
    Checkpoints,
    Sessions,
    Summary
}

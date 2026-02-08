using LucidRAG.Data;
using LucidRAG.UltraResearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch.Views;

public static class SessionView
{
    private static readonly TextBlockStyle LabelStyle = TextBlockStyle.Default with { Foreground = Colors.Gray };
    private static readonly TextBlockStyle ValueStyle = TextBlockStyle.Default with { Foreground = Colors.White };

    public static Visual Create(AppState appState, ServiceProvider services)
    {
        var resumeStatus = new State<string?>("");

        return new Group("Session Manager")
            .Content(new VStack(
                // Active session details
                new TextBlock(() =>
                {
                    var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();

                    if (appState.ActiveSessionId.Value is not { } sid)
                        return "  No active session.";

                    var status = orchestrator.GetStatus(sid);
                    if (status == null)
                        return $"  Session {sid:N} not found (may have completed).";

                    var elapsed = (status.CompletedAt ?? DateTimeOffset.UtcNow) - status.StartedAt;
                    var successRate = status.PapersFetched > 0
                        ? (double)status.PapersIngested / status.PapersFetched * 100
                        : 0.0;

                    return string.Join("\n", new[]
                    {
                        $"  Session:       {status.SessionId:N}",
                        $"  Topic:         {status.Topic}",
                        $"  Status:        {status.Status}",
                        $"  Collection:    {status.CollectionId:N}",
                        $"  Started:       {status.StartedAt:yyyy-MM-dd HH:mm:ss}",
                        $"  Elapsed:       {elapsed:hh\\:mm\\:ss}",
                        $"  Papers:        {status.PapersFetched} fetched, {status.PapersIngested} ingested, {status.PapersFailed} failed",
                        $"  Success Rate:  {successRate:F1}%",
                        $"  Frontier:      {status.FrontierSize} candidates",
                        $"  Seen:          {status.SeenIdsCount} unique IDs",
                        $"  Checkpoints:   {status.CheckpointCount}",
                        status.StopReason != null ? $"  Stop Reason:   {status.StopReason}" : ""
                    }.Where(s => s.Length > 0));
                }),

                // Success rate colored indicator
                new HStack(
                    new TextBlock("  Success Rate: ").Style(LabelStyle),
                    new TextBlock(() =>
                    {
                        if (appState.ActiveSessionId.Value is not { } sid) return "";
                        var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                        var status = orchestrator.GetStatus(sid);
                        if (status == null || status.PapersFetched == 0) return "";
                        var rate = (double)status.PapersIngested / status.PapersFetched * 100;
                        return $"{rate:F1}%";
                    }).Style(() =>
                    {
                        if (appState.ActiveSessionId.Value is not { } sid)
                            return ValueStyle;
                        var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                        var status = orchestrator.GetStatus(sid);
                        if (status == null || status.PapersFetched == 0)
                            return ValueStyle;
                        var rate = (double)status.PapersIngested / status.PapersFetched * 100;
                        return TextBlockStyle.Default with
                        {
                            Foreground = rate > 80 ? Colors.LimeGreen : rate > 50 ? Colors.Gold : Colors.Tomato
                        };
                    })
                ).IsVisible(() => appState.ActiveSessionId.Value != null),

                new TextBlock(""),

                // Active session controls
                new HStack(
                    new Button("Stop Session").Click(() =>
                    {
                        if (appState.ActiveSessionId.Value is { } sid)
                        {
                            var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                            orchestrator.Stop(sid);
                            appState.AddActivity("Session stop requested.");
                        }
                    }),
                    new Button("Clear Session").Click(() =>
                    {
                        appState.Reset();
                        appState.AddActivity("Session cleared from dashboard.");
                        appState.CurrentView.Value = ViewMode.Dashboard;
                    }),
                    new Button("Back to Dashboard").Click(() =>
                    {
                        appState.CurrentView.Value = ViewMode.Dashboard;
                    })
                ).Spacing(2),

                new TextBlock(""),

                // Resumable sessions section
                new Group("Previous Sessions (Resumable)")
                    .Content(new VStack(
                        new TextBlock(() =>
                        {
                            try
                            {
                                using var scope = services.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

                                var resumable = db.Collections
                                    .Where(c => c.Settings != null && c.Name.StartsWith("ultraresearch-"))
                                    .OrderByDescending(c => c.UpdatedAt)
                                    .Take(10)
                                    .Select(c => new { c.Id, c.Name, c.UpdatedAt, c.Settings })
                                    .ToList();

                                if (resumable.Count == 0)
                                    return "  No previous research sessions found.";

                                var lines = new List<string>
                                {
                                    $"  {"#",-4} {"Collection",-45} {"Updated",-20} {"Status",-12}",
                                    $"  {"---",-4} {new string('-', 43),-45} {new string('-', 18),-20} {new string('-', 10),-12}"
                                };

                                for (var i = 0; i < resumable.Count; i++)
                                {
                                    var r = resumable[i];
                                    var name = r.Name.Length > 43 ? r.Name[..40] + "..." : r.Name;
                                    var statusText = "Unknown";
                                    if (r.Settings != null)
                                    {
                                        if (r.Settings.Contains("\"Status\":\"Running\"")) statusText = "● Resumable";
                                        else if (r.Settings.Contains("\"Status\":\"Stopped\"")) statusText = "■ Stopped";
                                        else if (r.Settings.Contains("\"Status\":\"Completed\"")) statusText = "✓ Completed";
                                        else if (r.Settings.Contains("\"Status\":\"Failed\"")) statusText = "✗ Failed";
                                    }

                                    lines.Add($"  {i + 1,-4} {name,-45} {r.UpdatedAt:yyyy-MM-dd HH:mm,-20} {statusText,-12}");
                                }

                                return string.Join("\n", lines);
                            }
                            catch
                            {
                                return "  Could not load previous sessions.";
                            }
                        }),

                        new TextBlock(""),

                        new HStack(
                            new Button("Resume Latest").Click(async () =>
                            {
                                if (appState.ActiveSessionId.Value != null &&
                                    appState.Status.Value == UltraResearchStatus.Running)
                                {
                                    resumeStatus.Value = "A session is already running. Stop it first.";
                                    return;
                                }

                                try
                                {
                                    using var scope = services.CreateScope();
                                    var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

                                    var latest = await db.Collections
                                        .Where(c => c.Settings != null &&
                                                    c.Name.StartsWith("ultraresearch-") &&
                                                    c.Settings.Contains("\"Status\":\"Running\""))
                                        .OrderByDescending(c => c.UpdatedAt)
                                        .FirstOrDefaultAsync();

                                    if (latest == null)
                                    {
                                        resumeStatus.Value = "No resumable sessions found (only Running state can be resumed).";
                                        return;
                                    }

                                    var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                                    var ingester = services.GetRequiredService<IDocumentIngester>();

                                    resumeStatus.Value = $"Resuming session from collection {latest.Name}...";
                                    var sessionId = await orchestrator.ResumeAsync(latest.Id, ingester);

                                    if (sessionId.HasValue)
                                    {
                                        appState.ActiveSessionId.Value = sessionId.Value;
                                        appState.AddActivity($"Resumed session from {latest.Name}");
                                        appState.CurrentView.Value = ViewMode.Dashboard;
                                    }
                                    else
                                    {
                                        resumeStatus.Value = "Could not resume: session state invalid or already completed.";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    resumeStatus.Value = $"Resume failed: {ex.Message}";
                                }
                            })
                        ).Spacing(2),

                        new TextBlock(() => resumeStatus.Value ?? "")
                            .Style(() => (resumeStatus.Value ?? "").Contains("failed", StringComparison.OrdinalIgnoreCase)
                                || (resumeStatus.Value ?? "").Contains("already running", StringComparison.OrdinalIgnoreCase)
                                ? TextBlockStyle.Default with { Foreground = Colors.Tomato }
                                : TextBlockStyle.Default with { Foreground = Colors.DeepSkyBlue })
                    ).Spacing(0))
            ).Spacing(1));
    }
}

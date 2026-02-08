using System.Text;
using LucidRAG.UltraResearch;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace LucidResearch.Views;

public static class CheckpointView
{
    public static Visual Create(AppState appState, ServiceProvider services)
    {
        return new Group("Sentinel Checkpoints")
            .Content(new VStack(
                // Checkpoint table
                new TextBlock(() =>
                {
                    if (appState.ActiveSessionId.Value is not { } sid)
                        return "No active session. Press F2 to start research.";

                    var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                    var checkpoints = orchestrator.GetCheckpointsSnapshot(sid, 20);

                    if (checkpoints is not { Count: > 0 })
                        return "No checkpoints yet. Sentinel runs every N papers.";

                    // Use StringBuilder to avoid List<string> + string.Join allocations
                    var sb = new StringBuilder();
                    sb.AppendLine($"  {"Iter",-6} {"Papers",-8} {"Entities",-10} {"NewInfo",-10} {"Gaps",-6} {"Queries",-9} {"Continue?",-10}");
                    sb.AppendLine($"  {"----",-6} {"------",-8} {"--------",-10} {"-------",-10} {"----",-6} {"-------",-9} {"---------",-10}");

                    // Checkpoints come in reverse order (most recent first) — iterate
                    // backwards to display in ascending iteration order
                    for (var i = checkpoints.Count - 1; i >= 0; i--)
                    {
                        var cp = checkpoints[i];
                        sb.AppendLine(
                            $"  {cp.Iteration,-6} {cp.TotalPapers,-8} {cp.TotalEntities,-10} {cp.NewInfoRatio,-10:F3} {cp.IdentifiedGaps.Count,-6} {cp.SuggestedQueries.Count,-9} {(cp.ShouldContinue ? "Yes" : "No"),-10}");
                    }

                    return sb.ToString();
                }),

                new TextBlock(""),

                // Latest checkpoint details — reuse the same snapshot call
                new TextBlock(() =>
                {
                    if (appState.ActiveSessionId.Value is not { } sid)
                        return "";

                    var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                    var checkpoints = orchestrator.GetCheckpointsSnapshot(sid, 1);

                    if (checkpoints is not { Count: > 0 })
                        return "";

                    var latest = checkpoints[0];
                    var sb = new StringBuilder();
                    sb.AppendLine($"  --- Latest Checkpoint (Iteration {latest.Iteration}) ---");

                    if (latest.IdentifiedGaps.Count > 0)
                    {
                        sb.AppendLine("  Gaps:");
                        foreach (var gap in latest.IdentifiedGaps.Take(5))
                            sb.AppendLine($"    - {gap}");
                    }

                    if (latest.SuggestedQueries.Count > 0)
                    {
                        sb.AppendLine("  Suggested Queries:");
                        foreach (var q in latest.SuggestedQueries.Take(5))
                            sb.AppendLine($"    - {q}");
                    }

                    if (!string.IsNullOrEmpty(latest.SentinelAnalysis))
                    {
                        sb.AppendLine("  Analysis:");
                        var analysis = latest.SentinelAnalysis;
                        if (analysis.Length > 300)
                            analysis = analysis[..297] + "...";
                        sb.AppendLine($"    {analysis}");
                    }

                    return sb.ToString();
                })
            ).Spacing(0));
    }
}

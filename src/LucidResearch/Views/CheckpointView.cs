using System.Text;
using LucidRAG.UltraResearch;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch.Views;

public static class CheckpointView
{
    private static readonly TextBlockStyle LabelStyle = TextBlockStyle.Default with { Foreground = Colors.Gray };

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

                    var sb = new StringBuilder();
                    sb.AppendLine($"  {"Iter",-6} {"Papers",-8} {"Entities",-10} {"NewInfo",-10} {"Gaps",-6} {"Queries",-9} {"Continue?",-10}");
                    sb.AppendLine($"  {"----",-6} {"------",-8} {"--------",-10} {"-------",-10} {"----",-6} {"-------",-9} {"---------",-10}");

                    for (var i = checkpoints.Count - 1; i >= 0; i--)
                    {
                        var cp = checkpoints[i];

                        // NewInfoRatio indicator
                        var infoIndicator = cp.NewInfoRatio switch
                        {
                            > 0.5 => "▲", // still exploring
                            > 0.2 => "●", // slowing
                            _ => "▽"      // converging
                        };

                        // Continue indicator
                        var continueText = cp.ShouldContinue ? "Yes ✓" : "No ✗";

                        sb.AppendLine(
                            $"  {cp.Iteration,-6} {cp.TotalPapers,-8} {cp.TotalEntities,-10} {infoIndicator} {cp.NewInfoRatio,-8:F3} {cp.IdentifiedGaps.Count,-6} {cp.SuggestedQueries.Count,-9} {continueText,-10}");
                    }

                    return sb.ToString();
                }),

                new TextBlock(""),

                // Latest checkpoint details
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
                }),

                new TextBlock("  ▲ exploring  ● slowing  ▽ converging").Style(LabelStyle)
            ).Spacing(0));
    }
}

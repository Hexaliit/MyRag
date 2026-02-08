using System.Text;
using LucidRAG.UltraResearch;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace LucidResearch.Views;

public static class FrontierView
{
    public static Visual Create(AppState appState, ServiceProvider services)
    {
        return new Group("Research Frontier")
            .Content(new VStack(
                new TextBlock(() =>
                {
                    if (appState.ActiveSessionId.Value is not { } sid)
                        return "No active session. Press F2 to start research.";

                    var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                    var candidates = orchestrator.GetFrontierSnapshot(sid, 50);

                    if (candidates is not { Count: > 0 })
                        return "Frontier is empty.";

                    // Use StringBuilder to avoid List<string> + string.Join allocations
                    var sb = new StringBuilder();
                    sb.AppendLine($"  {"#",-4} {"Priority",-10} {"Type",-8} {"Source",-15} {"Citations",-12} {"Title",-50}");
                    sb.AppendLine($"  {"---",-4} {"--------",-10} {"----",-8} {"-----------",-15} {"--------",-12} {"-----",-50}");

                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var c = candidates[i];
                        var title = c.Title ?? c.Id;
                        if (title.Length > 50) title = title[..47] + "...";
                        sb.AppendLine(
                            $"  {i + 1,-4} {c.Priority,-10:F3} {c.Type,-8} {c.Source,-15} {c.CitedByCount,-12} {title,-50}");
                    }

                    return sb.ToString();
                }),
                new TextBlock(""),
                new TextBlock(() =>
                    appState.ActiveSessionId.Value != null
                        ? $"  Showing top {Math.Min(50, appState.FrontierSize.Value)} of {appState.FrontierSize.Value} candidates"
                        : "")
            ).Spacing(0));
    }
}

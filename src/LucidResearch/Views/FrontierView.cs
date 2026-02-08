using System.Text;
using LucidRAG.UltraResearch;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch.Views;

public static class FrontierView
{
    private static readonly TextBlockStyle LabelStyle = TextBlockStyle.Default with { Foreground = Colors.Gray };

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

                    var sb = new StringBuilder();
                    sb.AppendLine($"  {"#",-4} {"Priority",-10} {"Type",-8} {"Source",-15} {"Citations",-12} {"Title",-50}");
                    sb.AppendLine($"  {"---",-4} {"--------",-10} {"----",-8} {"-----------",-15} {"--------",-12} {"-----",-50}");

                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var c = candidates[i];
                        var title = c.Title ?? c.Id;
                        if (title.Length > 50) title = title[..47] + "...";

                        // Priority indicator: colored via Unicode block chars
                        var priorityIndicator = c.Priority switch
                        {
                            > 0.7 => "▲",  // high
                            > 0.3 => "●",  // medium
                            _ => "▽"       // low
                        };

                        // Source indicator
                        var sourceTag = c.Source switch
                        {
                            CandidateSource.Search => "[S]",
                            CandidateSource.Citation => "[C]",
                            CandidateSource.SemanticScholar => "[S2]",
                            CandidateSource.Sentinel => "[!]",
                            CandidateSource.Orphan => "[O]",
                            _ => "[ ]"
                        };

                        sb.AppendLine(
                            $"  {i + 1,-4} {priorityIndicator} {c.Priority,-8:F3} {c.Type,-8} {sourceTag} {c.Source,-11} {c.CitedByCount,-12} {title,-50}");
                    }

                    return sb.ToString();
                }),
                new TextBlock(""),
                new HStack(
                    new TextBlock(() =>
                        appState.ActiveSessionId.Value != null
                            ? $"  Showing top {Math.Min(50, appState.FrontierSize.Value)} of {appState.FrontierSize.Value} candidates"
                            : "").Style(LabelStyle),
                    new TextBlock("  ▲ high  ● mid  ▽ low  │  [S] Search  [C] Citation  [S2] SemanticScholar  [!] Sentinel  [O] Orphan")
                        .Style(LabelStyle)
                )
            ).Spacing(0));
    }
}

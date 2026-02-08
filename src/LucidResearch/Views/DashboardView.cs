using System.Text;
using LucidRAG.UltraResearch;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch.Views;

public static class DashboardView
{
    private static readonly TextBlockStyle LabelStyle = TextBlockStyle.Default with { Foreground = Colors.Gray };
    private static readonly TextBlockStyle ValueStyle = TextBlockStyle.Default with { Foreground = Colors.White };
    private static readonly TextBlockStyle GreenValue = TextBlockStyle.Default with { Foreground = Colors.LimeGreen };
    private static readonly TextBlockStyle BlueValue = TextBlockStyle.Default with { Foreground = Colors.DeepSkyBlue };
    private static readonly TextBlockStyle RedValue = TextBlockStyle.Default with { Foreground = Colors.Tomato };

    public static Visual Create(AppState appState)
    {
        return new VStack(
            // Top row: Metrics + Convergence side-by-side
            new HStack(
                // Left: Metrics panel
                new Group("Metrics")
                    .Content(new VStack(
                        // Status — dynamically colored
                        new HStack(
                            new TextBlock("  Status:      ").Style(LabelStyle),
                            new TextBlock(() => FormatStatus(appState.Status.Value))
                                .Style(() => TextBlockStyle.Default with { Foreground = ResearchApp.StatusColor(appState.Status.Value) })
                        ),
                        new HStack(
                            new TextBlock("  Topic:       ").Style(LabelStyle),
                            new TextBlock(() => Truncate(appState.Topic.Value, 40)).Style(ValueStyle)
                        ),
                        new HStack(
                            new TextBlock("  Iteration:   ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.Iteration.Value}").Style(ValueStyle)
                        ),
                        new HStack(
                            new TextBlock("  Fetched:     ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.PapersFetched.Value}").Style(GreenValue)
                        ),
                        new HStack(
                            new TextBlock("  Ingested:    ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.PapersIngested.Value}").Style(BlueValue)
                        ),
                        new HStack(
                            new TextBlock("  Failed:      ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.PapersFailed.Value}").Style(RedValue)
                        ),
                        new HStack(
                            new TextBlock("  Frontier:    ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.FrontierSize.Value}").Style(ValueStyle)
                        ),
                        new HStack(
                            new TextBlock("  Seen:        ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.SeenCount.Value}").Style(ValueStyle)
                        ),
                        new HStack(
                            new TextBlock("  Entities:    ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.TotalEntities.Value}").Style(ValueStyle)
                        ),
                        new HStack(
                            new TextBlock("  Orphans:     ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.OrphanCitations.Value}").Style(ValueStyle)
                        )
                    ).Spacing(0)),

                // Right: Convergence panel
                new Group("Convergence")
                    .Content(new VStack(
                        new HStack(
                            new TextBlock("  NewInfoRatio: ").Style(LabelStyle),
                            new TextBlock(() => $"{appState.NewInfoRatio.Value:F3}")
                                .Style(() => NewInfoRatioStyle(appState.NewInfoRatio.Value))
                        ),
                        new ProgressBar(Math.Clamp(1.0 - appState.NewInfoRatio.Value, 0, 1)),
                        new TextBlock(""),
                        new TextBlock(() => FormatSparkline(appState.NewInfoHistory.Value)),
                        new TextBlock(""),
                        new TextBlock(() => appState.NewInfoHistory.Value.Count > 0
                            ? $"  {appState.NewInfoHistory.Value.Count} checkpoints recorded"
                            : "  No checkpoints yet").Style(LabelStyle)
                    ).Spacing(0))
            ).Spacing(1),

            // Bottom: Recent activity
            new Group("Recent Activity")
                .Content(new TextBlock(() =>
                    FormatActivityLog(appState.ActivityLog.Value)))

        ).Spacing(0);
    }

    private static TextBlockStyle NewInfoRatioStyle(double ratio) =>
        TextBlockStyle.Default with { Foreground = ratio > 0.5 ? Colors.LimeGreen : ratio > 0.2 ? Colors.Gold : Colors.Tomato };

    private static string FormatStatus(UltraResearchStatus status) => status switch
    {
        UltraResearchStatus.Running => "RUNNING",
        UltraResearchStatus.Completed => "COMPLETED",
        UltraResearchStatus.Stopped => "STOPPED",
        UltraResearchStatus.Failed => "FAILED",
        UltraResearchStatus.Paused => "PAUSED",
        _ => status.ToString().ToUpperInvariant()
    };

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";

    private static string FormatSparkline(List<double> values)
    {
        if (values.Count == 0) return "  (no data)";
        const string blocks = " \u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588";

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        if (range < 0.001) range = 1.0;

        Span<char> buffer = stackalloc char[values.Count + 2];
        buffer[0] = ' ';
        buffer[1] = ' ';
        for (var i = 0; i < values.Count; i++)
        {
            var idx = (int)((values[i] - min) / range * (blocks.Length - 1));
            buffer[i + 2] = blocks[Math.Clamp(idx, 0, blocks.Length - 1)];
        }

        return new string(buffer);
    }

    private static string FormatActivityLog(List<string> log)
    {
        if (log.Count == 0)
            return "  No activity yet. Press F2 to start a new research session.";

        var startIdx = Math.Max(0, log.Count - 10);
        var sb = new StringBuilder();
        for (var i = startIdx; i < log.Count; i++)
        {
            if (i > startIdx) sb.AppendLine();
            sb.Append("  ");
            sb.Append(log[i]);
        }
        return sb.ToString();
    }
}

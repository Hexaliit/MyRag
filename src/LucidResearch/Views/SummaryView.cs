using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch.Views;

public static class SummaryView
{
    private static readonly TextBlockStyle LabelStyle = TextBlockStyle.Default with { Foreground = Colors.Gray };

    public static Visual Create(AppState appState)
    {
        return new VStack(
            new Group("Research Synthesis")
                .Content(new VStack(
                    new TextBlock(() => FormatSynthesis(appState.SynthesisText.Value)),
                    new TextBlock(() => FormatFilePath(appState.SynthesisFilePath.Value))
                        .Style(LabelStyle)
                ).Spacing(1))
        ).Spacing(0);
    }

    private static string FormatSynthesis(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "  No synthesis available yet. Complete a research session first.";

        // Indent each line for consistent TUI padding
        var lines = text.Split('\n');
        return string.Join('\n', lines.Select(l => "  " + l));
    }

    private static string FormatFilePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        return $"  Saved to: {path}";
    }
}

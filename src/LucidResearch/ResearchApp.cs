using LucidResearch.Services;
using LucidResearch.Views;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace LucidResearch;

public static class ResearchApp
{
    public static async Task RunAsync(string[] args, ServiceProvider services)
    {
        var appState = services.GetRequiredService<AppState>();
        var statePoller = services.GetRequiredService<StatePoller>();

        // Start background polling
        statePoller.Start();

        try
        {
            var layout = BuildLayout(appState, services);

            Terminal.Run(layout, () =>
                appState.ExitRequested.Value
                    ? TerminalLoopResult.StopAndKeepVisual
                    : TerminalLoopResult.Continue
            );
        }
        finally
        {
            await statePoller.StopAsync();
            await services.DisposeAsync();
        }
    }

    private static Visual BuildLayout(AppState appState, ServiceProvider services)
    {
        var dashboard = DashboardView.Create(appState);
        var startResearch = StartResearchView.Create(appState, services);
        var frontier = FrontierView.Create(appState, services);
        var checkpoints = CheckpointView.Create(appState, services);
        var sessions = SessionView.Create(appState, services);

        var content = new VStack(
            dashboard.IsVisible(() => appState.CurrentView.Value == ViewMode.Dashboard),
            startResearch.IsVisible(() => appState.CurrentView.Value == ViewMode.StartResearch),
            frontier.IsVisible(() => appState.CurrentView.Value == ViewMode.Frontier),
            checkpoints.IsVisible(() => appState.CurrentView.Value == ViewMode.Checkpoints),
            sessions.IsVisible(() => appState.CurrentView.Value == ViewMode.Sessions)
        );

        var statusText = "F1 Dashboard | F2 New | F3 Frontier | F4 Checkpoints | F5 Sessions | q Quit";

        var layout = new DockLayout(
            new Header().Left(new TextBlock("lucidRESEARCH"))
                .Right(new TextBlock(() =>
                    appState.Status.Value == LucidRAG.UltraResearch.UltraResearchStatus.Running
                        ? "[Running]"
                        : "[Idle]")),
            content,
            new StatusBar(new TextBlock(statusText), null)
        );

        // Keyboard navigation
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F1, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Dashboard);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F2, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.StartResearch);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F3, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Frontier);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F4, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Checkpoints);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F5, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Sessions);
        layout.AddKeyBinding(new KeyGesture('q', TerminalModifiers.None), () => appState.ExitRequested.Value = true);

        return layout;
    }
}

using LucidRAG.UltraResearch;
using LucidResearch.Services;
using LucidResearch.Views;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch;

public static class ResearchApp
{
    public static async Task RunAsync(string[] args, ServiceProvider services)
    {
        var appState = services.GetRequiredService<AppState>();
        var statePoller = services.GetRequiredService<StatePoller>();

        // Start background polling
        statePoller.Start();

        // Handle quick-start after TUI launches
        if (appState.PendingQuickStart.Value is { } quickTopic)
        {
            appState.PendingQuickStart.Value = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    var config = new UltraResearchConfig { Topic = quickTopic };
                    var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                    var ingester = services.GetRequiredService<IDocumentIngester>();
                    var sessionId = await orchestrator.StartAsync(config, ingester);
                    appState.ActiveSessionId.Value = sessionId;
                    appState.AddActivity($"Quick-started: {quickTopic}");
                    appState.CurrentView.Value = ViewMode.Dashboard;
                }
                catch (Exception ex)
                {
                    appState.AddActivity($"Quick-start failed: {ex.Message}");
                }
            });
        }

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

        // Gradient branding for header
        var headerBrush = Brush.LinearGradient(
            new GradientPoint(0f, 0f), new GradientPoint(1f, 0f),
            [new GradientStop(0f, Colors.DeepSkyBlue), new GradientStop(1f, Colors.MediumPurple)]);

        var header = new Header()
            .Left(new TextBlock("lucidRESEARCH")
                .Style(TextBlockStyle.Default with { ForegroundBrush = headerBrush }))
            .Right(new TextBlock(() => FormatStatusBadge(appState.Status.Value))
                .Style(() => TextBlockStyle.Default with { Foreground = StatusColor(appState.Status.Value) }));

        var statusBar = new StatusBar(
            new TextBlock(() => FormatStatusBarText(appState.CurrentView.Value)), null);

        var layout = new DockLayout(header, content, statusBar);

        // Keyboard navigation
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F1, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Dashboard);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F2, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.StartResearch);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F3, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Frontier);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F4, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Checkpoints);
        layout.AddKeyBinding(new KeyGesture(TerminalKey.F5, TerminalModifiers.None), () => appState.CurrentView.Value = ViewMode.Sessions);
        layout.AddKeyBinding(new KeyGesture('q', TerminalModifiers.None), () => appState.ExitRequested.Value = true);

        return layout;
    }

    internal static Color StatusColor(UltraResearchStatus status) => status switch
    {
        UltraResearchStatus.Running => Colors.LimeGreen,
        UltraResearchStatus.Completed => Colors.DeepSkyBlue,
        UltraResearchStatus.Stopped => Colors.Orange,
        UltraResearchStatus.Failed => Colors.Tomato,
        UltraResearchStatus.Paused => Colors.Gold,
        _ => Colors.White
    };

    private static string FormatStatusBadge(UltraResearchStatus status) => status switch
    {
        UltraResearchStatus.Running => "● RUNNING",
        UltraResearchStatus.Completed => "✓ COMPLETE",
        UltraResearchStatus.Stopped => "■ STOPPED",
        UltraResearchStatus.Failed => "✗ FAILED",
        UltraResearchStatus.Paused => "⏸ PAUSED",
        _ => "○ IDLE"
    };

    private static string FormatStatusBarText(ViewMode currentView)
    {
        var views = new (string key, string label, ViewMode mode)[]
        {
            ("F1", "Dashboard", ViewMode.Dashboard),
            ("F2", "New", ViewMode.StartResearch),
            ("F3", "Frontier", ViewMode.Frontier),
            ("F4", "Checkpoints", ViewMode.Checkpoints),
            ("F5", "Sessions", ViewMode.Sessions),
        };

        var parts = views.Select(v =>
            v.mode == currentView ? $"[{v.key} {v.label}]" : $" {v.key} {v.label} ");

        return string.Join("│", parts) + " │ q Quit";
    }
}

using System.Text;
using LucidResearch.Services;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;

namespace LucidResearch.Views;

public static class ChatView
{
    private static readonly TextBlockStyle LabelStyle =
        TextBlockStyle.Default with { Foreground = Colors.Gray };

    private static readonly TextBlockStyle UserStyle =
        TextBlockStyle.Default with { Foreground = Colors.DeepSkyBlue };

    private static readonly TextBlockStyle ErrorStyle =
        TextBlockStyle.Default with { Foreground = Colors.Tomato };

    public static Visual Create(AppState appState, ServiceProvider services)
    {
        var chatInput = new State<string?>("");
        var statusMessage = new State<string?>("");

        return new Group("Research Q&A")
            .Content(new VStack(
                // Chat history display
                new TextBlock(() => appState.ChatDisplay.Value)
                    .IsVisible(() => !string.IsNullOrEmpty(appState.ChatDisplay.Value)),

                // No-session prompt
                new TextBlock("No active session. Press F5 to select a session, or F2 to start research.")
                    .Style(LabelStyle)
                    .IsVisible(() => appState.ActiveSessionId.Value == null),

                // Loading indicator
                new TextBlock(() => "Searching...")
                    .Style(TextBlockStyle.Default with { Foreground = Colors.Gold })
                    .IsVisible(() => appState.ChatLoading.Value),

                // Input area (only when session active)
                new TextBlock("Ask a question about your research:")
                    .Style(LabelStyle)
                    .IsVisible(() => appState.ActiveSessionId.Value != null && !appState.ChatLoading.Value),

                new TextBox(chatInput)
                    .IsVisible(() => appState.ActiveSessionId.Value != null && !appState.ChatLoading.Value),

                // Action buttons
                new HStack(
                    new Button("Ask (Enter)").Click(async () =>
                    {
                        await SubmitQuery(chatInput, appState, services, statusMessage);
                    }),
                    new Button("Clear").Click(() =>
                    {
                        appState.ChatDisplay.Value = "";
                        statusMessage.Value = null;
                    })
                ).Spacing(2)
                    .IsVisible(() => appState.ActiveSessionId.Value != null && !appState.ChatLoading.Value),

                // Status message
                new TextBlock(() => statusMessage.Value ?? "")
                    .Style(() => (statusMessage.Value ?? "").StartsWith("Error")
                        ? ErrorStyle
                        : TextBlockStyle.Default with { Foreground = Colors.Gray })
                    .IsVisible(() => !string.IsNullOrEmpty(statusMessage.Value))
            ).Spacing(1));
    }

    private static async Task SubmitQuery(
        State<string?> chatInput, AppState appState, ServiceProvider services, State<string?> statusMessage)
    {
        var query = chatInput.Value?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        chatInput.Value = "";
        appState.ChatLoading.Value = true;
        statusMessage.Value = null;

        // Append user query to display
        var display = appState.ChatDisplay.Value;
        display += $"\n  YOU: {query}\n";
        appState.ChatDisplay.Value = display;

        // Run search on background thread
        _ = Task.Run(async () =>
        {
            try
            {
                var chatService = services.GetRequiredService<ResearchChatService>();
                var result = await chatService.AskAsync(query);

                Dispatcher.Current.Post(() =>
                {
                    var sb = new StringBuilder(appState.ChatDisplay.Value);
                    sb.AppendLine($"  AI: {result.Answer}");

                    if (result.Sources.Count > 0)
                    {
                        sb.AppendLine();
                        sb.Append("  Sources: ");
                        sb.AppendLine(string.Join("  ",
                            result.Sources.Take(5).Select(s =>
                                $"[{s.Index}:{s.Type}]")));
                    }

                    sb.AppendLine($"  ({result.SegmentCount} segments, {result.SearchTimeMs}ms)");
                    sb.AppendLine(new string('─', 60));

                    appState.ChatDisplay.Value = sb.ToString();
                    appState.ChatLoading.Value = false;
                    statusMessage.Value = null;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Current.Post(() =>
                {
                    appState.ChatLoading.Value = false;
                    statusMessage.Value = $"Error: {ex.Message}";
                });
            }
        });
    }
}

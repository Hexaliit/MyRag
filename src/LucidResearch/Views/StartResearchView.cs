using LucidRAG.UltraResearch;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace LucidResearch.Views;

public static class StartResearchView
{
    public static Visual Create(AppState appState, ServiceProvider services)
    {
        var topic = new State<string?>("");
        var maxPapers = new State<string?>("200");
        var batchSize = new State<string?>("10");
        var maxIterations = new State<string?>("50");
        var seedArxivIds = new State<string?>("");
        var arxivCategories = new State<string?>("");
        var collectionName = new State<string?>("");
        var statusMessage = new State<string?>("");
        var showAdvanced = new State<bool>(false);

        var labelStyle = TextBlockStyle.Default with { Foreground = Colors.Gray };
        var promptStyle = TextBlockStyle.Default with { Foreground = Colors.DeepSkyBlue };

        return new Group("Start New Research")
            .Content(new VStack(
                // Prominent topic input
                new TextBlock("What do you want to research?").Style(promptStyle),
                new TextBox(topic),
                new TextBlock(""),

                // Action buttons
                new HStack(
                    new Button("Start Research").Click(async () =>
                    {
                        if (string.IsNullOrWhiteSpace(topic.Value))
                        {
                            statusMessage.Value = "Topic is required.";
                            return;
                        }

                        try
                        {
                            var config = new UltraResearchConfig
                            {
                                Topic = topic.Value!.Trim(),
                                MaxPapers = int.TryParse(maxPapers.Value, out var mp) ? mp : 200,
                                BatchSize = int.TryParse(batchSize.Value, out var bs) ? bs : 10,
                                MaxIterations = int.TryParse(maxIterations.Value, out var mi) ? mi : 50,
                                IncludeSemanticScholar = true
                            };

                            if (!string.IsNullOrWhiteSpace(seedArxivIds.Value))
                            {
                                config.SeedArxivIds = seedArxivIds.Value
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .ToList();
                            }

                            if (!string.IsNullOrWhiteSpace(arxivCategories.Value))
                            {
                                config.ArxivCategories = arxivCategories.Value
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .ToList();
                            }

                            if (!string.IsNullOrWhiteSpace(collectionName.Value))
                            {
                                config.CollectionName = collectionName.Value.Trim();
                            }

                            var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();
                            var ingester = services.GetRequiredService<IDocumentIngester>();

                            statusMessage.Value = "Starting research session...";
                            var sessionId = await orchestrator.StartAsync(config, ingester);

                            appState.ActiveSessionId.Value = sessionId;
                            appState.AddActivity($"Started research: {config.Topic} (session {sessionId:N})");
                            appState.CurrentView.Value = ViewMode.Dashboard;
                        }
                        catch (Exception ex)
                        {
                            statusMessage.Value = $"Error: {ex.Message}";
                        }
                    }),
                    new Button(() => showAdvanced.Value ? "Hide Advanced" : "Advanced...").Click(() =>
                    {
                        showAdvanced.Value = !showAdvanced.Value;
                    }),
                    new Button("Cancel").Click(() =>
                    {
                        appState.CurrentView.Value = ViewMode.Dashboard;
                    })
                ).Spacing(2),

                new TextBlock(""),

                // Collapsible advanced options
                new VStack(
                    new TextBlock("Advanced Options").Style(labelStyle),
                    new HStack(
                        new TextBlock("Max Papers:      ").Style(labelStyle),
                        new TextBox(maxPapers)
                    ),
                    new HStack(
                        new TextBlock("Batch Size:      ").Style(labelStyle),
                        new TextBox(batchSize)
                    ),
                    new HStack(
                        new TextBlock("Max Iterations:  ").Style(labelStyle),
                        new TextBox(maxIterations)
                    ),
                    new HStack(
                        new TextBlock("Seed arXiv IDs:  ").Style(labelStyle),
                        new TextBox(seedArxivIds)
                    ),
                    new HStack(
                        new TextBlock("Categories:      ").Style(labelStyle),
                        new TextBox(arxivCategories)
                    ),
                    new HStack(
                        new TextBlock("Collection Name: ").Style(labelStyle),
                        new TextBox(collectionName)
                    )
                ).Spacing(1).IsVisible(() => showAdvanced.Value),

                // Status message
                new TextBlock(() => statusMessage.Value ?? "")
                    .Style(() => string.IsNullOrEmpty(statusMessage.Value) || statusMessage.Value.StartsWith("Error")
                        ? TextBlockStyle.Default with { Foreground = Colors.Tomato }
                        : TextBlockStyle.Default with { Foreground = Colors.DeepSkyBlue })
            ).Spacing(1));
    }
}

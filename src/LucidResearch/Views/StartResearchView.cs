using LucidRAG.UltraResearch;
using Microsoft.Extensions.DependencyInjection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

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

        return new Group("Start New Research Session")
            .Content(new VStack(
                new HStack(
                    new TextBlock("Topic: "),
                    new TextBox(topic)
                ),
                new HStack(
                    new TextBlock("Max Papers: "),
                    new TextBox(maxPapers)
                ),
                new HStack(
                    new TextBlock("Batch Size: "),
                    new TextBox(batchSize)
                ),
                new HStack(
                    new TextBlock("Max Iterations: "),
                    new TextBox(maxIterations)
                ),
                new HStack(
                    new TextBlock("Seed arXiv IDs (comma-separated): "),
                    new TextBox(seedArxivIds)
                ),
                new HStack(
                    new TextBlock("arXiv Categories (comma-separated): "),
                    new TextBox(arxivCategories)
                ),
                new HStack(
                    new TextBlock("Collection Name (auto if empty): "),
                    new TextBox(collectionName)
                ),
                new TextBlock(""),
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
                    new Button("Cancel").Click(() =>
                    {
                        appState.CurrentView.Value = ViewMode.Dashboard;
                    })
                ).Spacing(2),
                new TextBlock(() => statusMessage.Value ?? "")
            ).Spacing(1));
    }
}

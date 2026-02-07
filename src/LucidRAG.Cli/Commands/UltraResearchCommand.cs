using System.CommandLine;
using DoomSummarizer.Services;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using LucidRAG.Services;
using LucidRAG.UltraResearch;
using LucidRAG.UltraResearch.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
///     Autonomous research corpus builder. Discovers, fetches, and indexes academic papers
///     using citation graph topology, entity clusters, and LLM sentinel checkpoints.
/// </summary>
public static class UltraResearchCommand
{
    private static readonly Argument<string> TopicArg = new("topic")
        { Description = "Research topic to explore" };

    private static readonly Option<int> MaxPapersOpt = new("--max-papers", "-m")
        { Description = "Max papers to fetch (default: 200)", DefaultValueFactory = _ => 200 };

    private static readonly Option<int> MaxHoursOpt = new("--max-hours")
        { Description = "Max duration in hours (default: 4)", DefaultValueFactory = _ => 4 };

    private static readonly Option<string?> CollectionOpt = new("--collection", "-c")
        { Description = "Collection name (auto-generated if omitted)" };

    private static readonly Option<string[]> SeedPaperOpt = new("--seed-paper", "-s")
        { Description = "Seed paper arXiv IDs or DOIs", AllowMultipleArgumentsPerToken = true };

    private static readonly Option<string[]> CategoriesOpt = new("--categories")
        { Description = "arXiv categories (e.g., cs.CL,cs.AI)", AllowMultipleArgumentsPerToken = true };

    private static readonly Option<bool> DryRunOpt = new("--dry-run")
        { Description = "Search + discover without ingesting", DefaultValueFactory = _ => false };

    private static readonly Option<bool> NoSemanticScholarOpt = new("--no-s2")
        { Description = "Disable Semantic Scholar (arXiv-only mode)", DefaultValueFactory = _ => false };

    private static readonly Option<bool> VerboseOpt = new("--verbose", "-v")
        { Description = "Verbose output", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("ultraresearch",
            "Autonomous research corpus builder — discovers, fetches, and indexes papers");
        command.Arguments.Add(TopicArg);
        command.Options.Add(MaxPapersOpt);
        command.Options.Add(MaxHoursOpt);
        command.Options.Add(CollectionOpt);
        command.Options.Add(SeedPaperOpt);
        command.Options.Add(CategoriesOpt);
        command.Options.Add(DryRunOpt);
        command.Options.Add(NoSemanticScholarOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var topic = parseResult.GetValue(TopicArg) ?? "";
            var maxPapers = parseResult.GetValue(MaxPapersOpt);
            var maxHours = parseResult.GetValue(MaxHoursOpt);
            var collectionName = parseResult.GetValue(CollectionOpt);
            var seedPapers = parseResult.GetValue(SeedPaperOpt) ?? [];
            var categories = parseResult.GetValue(CategoriesOpt) ?? [];
            var dryRun = parseResult.GetValue(DryRunOpt);
            var noS2 = parseResult.GetValue(NoSemanticScholarOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            AnsiConsole.Write(new FigletText("UltraResearch").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Autonomous Research Corpus Builder[/]\n");

            if (dryRun)
                AnsiConsole.MarkupLine("[yellow]DRY RUN[/] — search + discover without ingesting\n");

            // Classify seed papers
            var seedArxiv = new List<string>();
            var seedDois = new List<string>();
            foreach (var seed in seedPapers)
            {
                if (AcademicPatterns.ArxivBareIdRegex().IsMatch(seed))
                    seedArxiv.Add(seed);
                else if (AcademicPatterns.DoiBareIdRegex().IsMatch(seed))
                    seedDois.Add(seed);
                else
                {
                    var fromUrl = AcademicPatterns.ExtractArxivIdFromUrl(seed);
                    if (fromUrl != null) seedArxiv.Add(fromUrl);
                    else seedDois.Add(seed); // Assume DOI
                }
            }

            // Parse categories (comma-separated)
            var arxivCategories = categories
                .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToList();

            var config = new UltraResearchConfig
            {
                Topic = topic,
                MaxPapers = maxPapers,
                MaxDuration = TimeSpan.FromHours(maxHours),
                CollectionName = collectionName,
                SeedArxivIds = seedArxiv,
                SeedDois = seedDois,
                ArxivCategories = arxivCategories,
                IncludeSemanticScholar = !noS2,
                DryRun = dryRun,
                DataDirectory = Program.EnsureDataDirectory()
            };

            AnsiConsole.MarkupLine($"[cyan]Topic:[/] {Markup.Escape(topic)}");
            AnsiConsole.MarkupLine($"[cyan]Max papers:[/] {maxPapers}, [cyan]Max hours:[/] {maxHours}");
            if (seedArxiv.Count > 0)
                AnsiConsole.MarkupLine($"[cyan]Seed arXiv:[/] {string.Join(", ", seedArxiv)}");
            if (seedDois.Count > 0)
                AnsiConsole.MarkupLine($"[cyan]Seed DOIs:[/] {string.Join(", ", seedDois)}");
            if (arxivCategories.Count > 0)
                AnsiConsole.MarkupLine($"[cyan]Categories:[/] {string.Join(", ", arxivCategories)}");
            AnsiConsole.MarkupLine($"[cyan]Semantic Scholar:[/] {(noS2 ? "disabled" : "enabled")}");
            AnsiConsole.WriteLine();

            // Build services
            var cliConfig = new CliConfig
            {
                DataDirectory = config.DataDirectory!,
                Verbose = verbose
            };

            var services = CliServiceRegistration.BuildServiceProvider(cliConfig, verbose);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            // Register UltraResearch services
            // Since CliServiceRegistration builds its own container, we need to add UltraResearch manually
            var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            // Create adapters for services the orchestrator needs
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "LucidRAG/1.0 (https://github.com/scottgal/lucidrag)");

            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var arxivFetcher = new ArxivFetcher(httpClient);
            var citationResolver = new CitationResolver(httpClient, loggerFactory.CreateLogger<CitationResolver>());
            var s2Client = new SemanticScholarClient(httpClient, loggerFactory.CreateLogger<SemanticScholarClient>());

            // Check for S2 API key
            var s2Key = Environment.GetEnvironmentVariable("SEMANTIC_SCHOLAR_API_KEY");
            if (!string.IsNullOrEmpty(s2Key))
            {
                s2Client.SetApiKey(s2Key);
                if (verbose) AnsiConsole.MarkupLine("[dim]Using Semantic Scholar API key[/]");
            }

            var fetcher = new ResearchPaperFetcher(
                arxivFetcher, citationResolver, s2Client, httpClient,
                loggerFactory.CreateLogger<ResearchPaperFetcher>());

            var graphQueries = new CitationGraphQueries(db, loggerFactory.CreateLogger<CitationGraphQueries>());
            var frontierManager = new ResearchFrontierManager(graphQueries, loggerFactory.CreateLogger<ResearchFrontierManager>());

            // OllamaService for sentinel (optional)
            OllamaService? ollama = null;
            try
            {
                ollama = scope.ServiceProvider.GetService<OllamaService>();
            }
            catch
            {
                // OllamaService may not be registered
            }

            var sentinelEvaluator = new ResearchSentinelEvaluator(
                db, graphQueries, loggerFactory.CreateLogger<ResearchSentinelEvaluator>(), ollama);

            // Create document ingester adapter
            var processor = scope.ServiceProvider.GetRequiredService<CliDocumentProcessor>();
            var ingester = new CliDocumentIngester(processor);

            // Run the loop directly (no background task for CLI)
            await RunInteractiveLoopAsync(config, fetcher, frontierManager, sentinelEvaluator, ingester, db, ct);

            await services.DisposeAsync();
            return 0;
        });

        return command;
    }

    private static async Task RunInteractiveLoopAsync(
        UltraResearchConfig config,
        ResearchPaperFetcher fetcher,
        ResearchFrontierManager frontier,
        ResearchSentinelEvaluator sentinel,
        IDocumentIngester ingester,
        RagDocumentsDbContext db,
        CancellationToken ct)
    {
        var state = new UltraResearchState { Topic = config.Topic };

        // Create collection
        var collectionName = config.CollectionName
            ?? $"ultraresearch-{config.Topic.ToLowerInvariant().Replace(" ", "-")[..Math.Min(30, config.Topic.Length)]}-{DateTimeOffset.UtcNow:yyyyMMdd}";

        var collection = db.Collections.FirstOrDefault(c => c.Name == collectionName);
        if (collection == null)
        {
            collection = new LucidRAG.Entities.CollectionEntity
            {
                Id = Guid.NewGuid(),
                Name = collectionName,
                Description = $"UltraResearch corpus: {config.Topic}"
            };
            db.Collections.Add(collection);
            await db.SaveChangesAsync(ct);
            AnsiConsole.MarkupLine($"[green]Created collection:[/] {Markup.Escape(collectionName)}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[cyan]Using existing collection:[/] {Markup.Escape(collectionName)}");
        }

        state.CollectionId = collection.Id;

        // Seed frontier
        foreach (var arxivId in config.SeedArxivIds)
        {
            var key = ResearchPaperFetcher.NormalizeSeenKey("arxiv", arxivId);
            if (state.SeenIds.Add(key))
                state.Frontier.Add(new FetchCandidate
                {
                    Id = arxivId, Type = "arxiv", Source = CandidateSource.Search, Priority = 1.0
                });
        }

        foreach (var doi in config.SeedDois)
        {
            var key = ResearchPaperFetcher.NormalizeSeenKey("doi", doi);
            if (state.SeenIds.Add(key))
                state.Frontier.Add(new FetchCandidate
                {
                    Id = doi, Type = "doi", Source = CandidateSource.Search, Priority = 1.0
                });
        }

        var startTime = DateTimeOffset.UtcNow;
        var papersSinceLastSentinel = 0;
        var consecutiveLowInfo = 0;

        // Initial search
        AnsiConsole.MarkupLine($"\n[cyan]Searching for:[/] {Markup.Escape(config.Topic)}");
        var initialCandidates = await fetcher.SearchAsync(config.Topic, config, state.SeenIds, ct);
        frontier.AddDiscoveredCandidates(initialCandidates, state);
        state.SearchQueriesUsed.Add(config.Topic);
        AnsiConsole.MarkupLine($"[dim]Found {initialCandidates.Count} candidates, frontier: {state.Frontier.Count}[/]");

        // Main loop with live display
        await AnsiConsole.Live(BuildStatusTable(state))
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested)
                {
                    state.Iteration++;
                    ctx.UpdateTarget(BuildStatusTable(state));

                    // Budget checks
                    if (state.PapersFetched >= config.MaxPapers)
                    {
                        state.StopReason = "Budget exhausted";
                        break;
                    }

                    if (DateTimeOffset.UtcNow - startTime > config.MaxDuration)
                    {
                        state.StopReason = "Duration limit";
                        break;
                    }

                    // Select batch
                    var batch = frontier.GetNextBatch(state, config.BatchSize);
                    if (batch.Count == 0)
                    {
                        // Try variations
                        var lastCp = state.Checkpoints.LastOrDefault();
                        if (lastCp?.SuggestedQueries.Count > 0)
                        {
                            foreach (var q in lastCp.SuggestedQueries.Where(q => state.SearchQueriesUsed.Add(q)))
                            {
                                var r = await fetcher.SearchAsync(q, config, state.SeenIds, ct);
                                frontier.AddDiscoveredCandidates(r, state);
                            }

                            batch = frontier.GetNextBatch(state, config.BatchSize);
                        }

                        if (batch.Count == 0)
                        {
                            state.StopReason = "Frontier exhausted";
                            break;
                        }
                    }

                    // Fetch + ingest batch
                    foreach (var candidate in batch)
                    {
                        if (ct.IsCancellationRequested) break;

                        var paper = await fetcher.FetchAndPrepareAsync(candidate, config.DataDirectory!, ct);
                        if (paper == null)
                        {
                            state.PapersFailed++;
                            continue;
                        }

                        state.PapersFetched++;
                        ctx.UpdateTarget(BuildStatusTable(state));

                        // Extract new citations
                        var newCandidates = paper.CitationIds
                            .Where(c => state.SeenIds.Add(ResearchPaperFetcher.NormalizeSeenKey(c.type, c.id)))
                            .Select(c => new FetchCandidate
                            {
                                Id = c.id, Type = c.type, Source = CandidateSource.Citation,
                                DiscoveredFrom = candidate.Id
                            })
                            .ToList();
                        frontier.AddDiscoveredCandidates(newCandidates, state);

                        // Reverse citations
                        if (config.IncludeSemanticScholar)
                        {
                            var reverse = await fetcher.GetReverseCitationsAsync(candidate, state.SeenIds, ct);
                            frontier.AddDiscoveredCandidates(reverse, state);
                        }

                        // Ingest
                        if (!config.DryRun)
                        {
                            var result = await ingester.IngestAsync(paper.FilePath, state.CollectionId, ct);
                            if (result.Success)
                            {
                                state.PapersIngested++;
                                papersSinceLastSentinel++;
                            }
                        }
                        else
                        {
                            state.PapersIngested++;
                            papersSinceLastSentinel++;
                        }

                        ctx.UpdateTarget(BuildStatusTable(state));
                    }

                    // Analyze
                    if (!config.DryRun)
                        await frontier.RefreshFrontierAsync(state, state.CollectionId, ct);

                    // Sentinel
                    if (papersSinceLastSentinel >= config.SentinelInterval && !config.DryRun)
                    {
                        var checkpoint = await sentinel.EvaluateAsync(state, state.CollectionId, ct);
                        state.Checkpoints.Add(checkpoint);
                        papersSinceLastSentinel = 0;

                        foreach (var q in checkpoint.SuggestedQueries.Where(q => state.SearchQueriesUsed.Add(q)))
                        {
                            var r = await fetcher.SearchAsync(q, config, state.SeenIds, ct);
                            frontier.AddDiscoveredCandidates(r, state);
                        }

                        if (checkpoint.NewInfoRatio < config.ConvergenceThreshold)
                            consecutiveLowInfo++;
                        else
                            consecutiveLowInfo = 0;

                        if (consecutiveLowInfo >= 3)
                        {
                            state.StopReason = "Converged";
                            break;
                        }

                        ctx.UpdateTarget(BuildStatusTable(state));
                    }
                }
            });

        // Summary
        state.Status = ct.IsCancellationRequested ? UltraResearchStatus.Stopped : UltraResearchStatus.Completed;
        state.CompletedAt = DateTimeOffset.UtcNow;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Session Complete[/]").RuleStyle("dim"));

        var summaryTable = new Table().Border(TableBorder.Rounded);
        summaryTable.AddColumn("Metric");
        summaryTable.AddColumn("Value");
        summaryTable.AddRow("Topic", Markup.Escape(config.Topic));
        summaryTable.AddRow("Collection", Markup.Escape(collectionName));
        summaryTable.AddRow("Iterations", state.Iteration.ToString());
        summaryTable.AddRow("Papers Fetched", state.PapersFetched.ToString());
        summaryTable.AddRow("Papers Ingested", state.PapersIngested.ToString());
        summaryTable.AddRow("Papers Failed", state.PapersFailed.ToString());
        summaryTable.AddRow("Frontier Remaining", state.Frontier.Count.ToString());
        summaryTable.AddRow("Sentinel Checkpoints", state.Checkpoints.Count.ToString());
        summaryTable.AddRow("Stop Reason", Markup.Escape(state.StopReason ?? "N/A"));
        summaryTable.AddRow("Duration", (DateTimeOffset.UtcNow - state.StartedAt).ToString(@"hh\:mm\:ss"));
        AnsiConsole.Write(summaryTable);

        if (config.DryRun)
            AnsiConsole.MarkupLine("\n[yellow]Dry run complete — no papers were ingested.[/]");
        else
            AnsiConsole.MarkupLine($"\n[green]Collection '{Markup.Escape(collectionName)}' is ready for chat![/]");
    }

    private static Table BuildStatusTable(UltraResearchState state)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[cyan]UltraResearch[/]");
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Iteration", state.Iteration.ToString());
        table.AddRow("Fetched", state.PapersFetched.ToString());
        table.AddRow("Ingested", state.PapersIngested.ToString());
        table.AddRow("Failed", state.PapersFailed.ToString());
        table.AddRow("Frontier", state.Frontier.Count.ToString());
        table.AddRow("Seen IDs", state.SeenIds.Count.ToString());

        var lastCp = state.Checkpoints.LastOrDefault();
        if (lastCp != null)
        {
            table.AddRow("New Info Ratio", $"{lastCp.NewInfoRatio:F3}");
            table.AddRow("Entities", lastCp.TotalEntities.ToString());
            table.AddRow("Orphans", lastCp.OrphanCitations.ToString());
        }

        return table;
    }
}

/// <summary>
///     Adapter to bridge CliDocumentProcessor to IDocumentIngester interface.
/// </summary>
internal class CliDocumentIngester(CliDocumentProcessor processor) : IDocumentIngester
{
    public async Task<DocumentIngestResult> IngestAsync(string filePath, Guid collectionId, CancellationToken ct)
    {
        var result = await processor.IndexFileAsync(filePath, collectionId, ct);
        return new DocumentIngestResult(result.Success, result.Message, result.DocumentId, result.SegmentCount);
    }
}

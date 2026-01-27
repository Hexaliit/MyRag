using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Crawl a website to build a searchable knowledge base.
/// Follows same-domain links from a seed URL, extracts content,
/// computes embeddings, and stores for later querying via 'scroll --local --kb [name]'.
/// </summary>
public sealed class CrawlCommand : AsyncCommand<CrawlCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        [Description("Seed URL to start crawling from")]
        public string Url { get; init; } = "";

        [CommandOption("-n|--name")]
        [Description("Knowledge base name (used to query later)")]
        public string? Name { get; init; }

        [CommandOption("-d|--depth")]
        [Description("Maximum crawl depth from seed URL")]
        [DefaultValue(3)]
        public int Depth { get; init; } = 3;

        [CommandOption("-m|--max-pages")]
        [Description("Maximum pages to crawl")]
        [DefaultValue(200)]
        public int MaxPages { get; init; } = 200;

        [CommandOption("--delay")]
        [Description("Delay between requests in milliseconds (politeness)")]
        [DefaultValue(500)]
        public int DelayMs { get; init; } = 500;

        [CommandOption("--concurrency")]
        [Description("Maximum concurrent requests")]
        [DefaultValue(3)]
        public int Concurrency { get; init; } = 3;

        [CommandOption("-g|--glob")]
        [Description("URL path filter pattern (e.g., /blog/* or /docs/*). Only pages matching this pattern are indexed.")]
        public string? Glob { get; init; }

        [CommandOption("--entities")]
        [Description("Enable NER entity extraction")]
        public bool Entities { get; init; }

        [CommandOption("-q|--quiet")]
        [Description("Minimal output")]
        public bool Quiet { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Url) || !Uri.TryCreate(settings.Url, UriKind.Absolute, out var seedUri))
        {
            AnsiConsole.MarkupLine("[red]Error: Please provide a valid URL to crawl.[/]");
            return 1;
        }

        // Derive KB name from domain if not provided
        var kbName = settings.Name ?? seedUri.Host.Replace("www.", "").Split('.')[0];

        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        await using var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        using var embedding = new EmbeddingService();
        await embedding.EnsureReadyAsync(msg =>
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
        });

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var crawlConfig = new CrawlConfig
        {
            Name = kbName,
            MaxDepth = settings.Depth,
            MaxPages = settings.MaxPages,
            DelayMs = settings.DelayMs,
            MaxConcurrency = settings.Concurrency,
            TimeoutSeconds = 15,
            PathFilter = settings.Glob
        };

        var crawler = new WebCrawlerService(httpClient, crawlConfig);

        AnsiConsole.MarkupLine($"[bold cyan]Crawling:[/] {Markup.Escape(settings.Url)}");
        var filterInfo = !string.IsNullOrEmpty(settings.Glob) ? $" | filter: {settings.Glob}" : "";
        AnsiConsole.MarkupLine($"[grey]KB name: {Markup.Escape(kbName)} | depth: {settings.Depth} | max: {settings.MaxPages} pages{filterInfo}[/]");
        AnsiConsole.WriteLine();

        var crawledItems = new List<Models.ContentItem>();

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Stage 1: Crawl and extract content
                var crawlTask = ctx.AddTask("[cyan]Crawling pages[/]", maxValue: settings.MaxPages);

                await foreach (var item in crawler.CrawlAsync(
                    settings.Url,
                    progress: new Progress<(int visited, int queued, int extracted)>(p =>
                    {
                        crawlTask.Value = Math.Min(p.visited, settings.MaxPages);
                        crawlTask.Description = $"[cyan]Crawling ({p.visited} visited, {p.extracted} extracted, {p.queued} queued)[/]";
                    }),
                    onActivity: activity =>
                    {
                        if (!settings.Quiet)
                            crawlTask.Description = $"[cyan]{Markup.Escape(activity)}[/]";
                    },
                    ct: cancellationToken))
                {
                    crawledItems.Add(item);
                }

                crawlTask.Value = settings.MaxPages;
                crawlTask.Description = $"[green]Crawled {crawler.PagesVisited} pages, extracted {crawler.PagesExtracted}, skipped {crawler.PagesSkipped}[/]";

                // Stage 2: Compute embeddings
                var embedTask = ctx.AddTask("[cyan]Computing embeddings[/]", maxValue: crawledItems.Count);

                foreach (var item in crawledItems)
                {
                    var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                    if (textToEmbed.Length > 1000)
                        textToEmbed = textToEmbed[..1000];
                    item.Embedding = embedding.Embed(textToEmbed);
                    embedTask.Increment(1);
                }

                embedTask.Description = $"[green]Embedded {crawledItems.Count} pages[/]";

                // Stage 3: NER entity extraction (optional)
                if (settings.Entities)
                {
                    var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: crawledItems.Count);
                    using var nerService = new NerService();
                    if (nerService.IsAvailable)
                    {
                        await nerService.InitializeAsync();
                        foreach (var item in crawledItems)
                        {
                            var textForNer = $"{item.Title} {item.Content?[..Math.Min(item.Content.Length, 1000)] ?? ""}";
                            var entities = await nerService.ExtractEntitiesAsync(textForNer);
                            // Store entity text in the item summary for searchability
                            if (entities.Count > 0)
                            {
                                var entityText = string.Join(", ", entities
                                    .OrderByDescending(e => e.Confidence)
                                    .Take(10)
                                    .Select(e => $"{e.Text} ({e.Type})"));
                                item.Summary = (item.Summary ?? "") + $" [Entities: {entityText}]";
                            }
                            nerTask.Increment(1);
                        }
                    }
                    nerTask.Description = $"[green]Entities extracted from {crawledItems.Count} pages[/]";
                }

                // Stage 4: Store in knowledge base
                var storeTask = ctx.AddTask("[cyan]Saving to knowledge base[/]", maxValue: crawledItems.Count);

                // Add topic and sentiment from embeddings
                var positiveAnchor = embedding.Embed(RelevanceScorer.PositiveAnchorText);
                var negativeAnchor = embedding.Embed(RelevanceScorer.NegativeAnchorText);
                var topicAnchors = RelevanceScorer.TopicAnchorTexts.ToDictionary(
                    kv => kv.Key,
                    kv => embedding.Embed(kv.Value));

                foreach (var item in crawledItems)
                {
                    if (item.Embedding != null)
                    {
                        item.SentimentScore = RelevanceScorer.ComputeEmbeddingSentiment(
                            item.Embedding, positiveAnchor, negativeAnchor);
                        item.DetectedTopic = RelevanceScorer.InferTopic(item.Embedding, topicAnchors);
                    }

                    item.Summary ??= item.Content?.Length > 300
                        ? item.Content[..300] + "..."
                        : item.Content ?? item.Title;

                    await storage.SaveItemAsync(item);
                    storeTask.Increment(1);
                }

                storeTask.Description = $"[green]Saved {crawledItems.Count} pages to KB '{kbName}'[/]";
            });

        // Summary
        AnsiConsole.WriteLine();

        var topicDist = crawledItems
            .Where(i => !string.IsNullOrEmpty(i.DetectedTopic))
            .GroupBy(i => i.DetectedTopic!)
            .OrderByDescending(g => g.Count())
            .Take(5);

        var table = new Table()
            .Title($"[bold cyan]Knowledge Base: {Markup.Escape(kbName)}[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Pages crawled", $"{crawler.PagesVisited}");
        table.AddRow("Pages extracted", $"{crawler.PagesExtracted}");
        table.AddRow("Pages skipped", $"{crawler.PagesSkipped}");
        table.AddRow("With embeddings", $"{crawledItems.Count(i => i.Embedding != null)}");
        table.AddRow("Topics", string.Join(", ", topicDist.Select(g => $"{g.Key} ({g.Count()})")));
        table.AddRow("Avg quality", crawledItems.Where(i => i.ContentStructure != null)
            .Select(i => i.ContentStructure!.QualityScore)
            .DefaultIfEmpty(0)
            .Average()
            .ToString("F2"));

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\n[grey]Query this KB with:[/] doomsummarizer scroll \"your question\" --name {Markup.Escape(kbName)}");
        AnsiConsole.MarkupLine($"[grey]Browse contents:[/] doomsummarizer show {Markup.Escape(kbName)}");

        return 0;
    }
}

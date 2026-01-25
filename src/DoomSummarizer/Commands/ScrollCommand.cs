using System.ComponentModel;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class ScrollCommand : AsyncCommand<ScrollCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[prompt]")]
        [Description("Natural language prompt (e.g., 'summarize bbc and hacker news') or URL")]
        public string? Prompt { get; init; }

        [CommandOption("-v|--vibe")]
        [Description("Sentiment steering: doom, hopeful, snarky, neutral")]
        [DefaultValue("neutral")]
        public string Vibe { get; init; } = "neutral";

        [CommandOption("-o|--output")]
        [Description("Output file path (.md, .txt, .html, .json) - format auto-detected")]
        public string? Output { get; init; }

        [CommandOption("-t|--template")]
        [Description("Output template: default, console, compact, detailed, file, email, newsletter, slack, json")]
        [DefaultValue("default")]
        public string Template { get; init; } = "default";

        [CommandOption("-s|--source")]
        [Description("Override sources (hn, reddit, search:query, or URL)")]
        public string[]? Sources { get; init; }

        [CommandOption("-l|--limit")]
        [Description("Maximum items to fetch")]
        [DefaultValue(30)]
        public int Limit { get; init; } = 30;

        [CommandOption("-f|--force")]
        [Description("Ignore cache and fetch fresh")]
        public bool Force { get; init; }

        [CommandOption("-q|--quiet")]
        [Description("Minimal output, just the summary")]
        public bool Quiet { get; init; }

        [CommandOption("--no-llm")]
        [Description("Skip LLM analysis, just list items")]
        public bool NoLlm { get; init; }

        [CommandOption("--json")]
        [Description("Output as JSON (for LLM tool consumption)")]
        public bool Json { get; init; }

        [CommandOption("--entities")]
        [Description("Extract and display named entities (people, orgs, locations)")]
        public bool ShowEntities { get; init; }

        [CommandOption("--raw")]
        [Description("Show raw fetched content before processing")]
        public bool ShowRaw { get; init; }

        [CommandOption("--images")]
        [Description("Display inline images for important items")]
        public bool ShowImages { get; init; }

        [CommandOption("--list-templates")]
        [Description("List available output templates")]
        public bool ListTemplates { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Handle --list-templates
        if (settings.ListTemplates)
        {
            var templateService = new TemplateService();
            AnsiConsole.MarkupLine("[bold cyan]Available Templates:[/]");
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Template")
                .AddColumn("Best For");

            table.AddRow("default", "Standard markdown output");
            table.AddRow("console", "Compact console display");
            table.AddRow("compact", "Minimal bullet list");
            table.AddRow("detailed", "Full details with sentiment");
            table.AddRow("file", "Clean markdown for file export");
            table.AddRow("email", "HTML email with inline styles");
            table.AddRow("newsletter", "Professional newsletter HTML");
            table.AddRow("slack", "Slack-formatted message");
            table.AddRow("json", "Raw JSON for API/automation");
            table.AddRow("image", "Single item with featured image");

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("\n[grey]Custom templates: place .liquid files in ~/.doomsummarizer/templates/[/]");
            return 0;
        }

        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        await using var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        // Initialize template service
        var templateService2 = new TemplateService();
        var templatesDir = Path.Combine(ConfigService.GetConfigDir(), "templates");
        await templateService2.LoadCustomTemplatesAsync(templatesDir);

        using var embedding = new EmbeddingService();
        var ollama = new OllamaService(config.Ollama);

        // Check prerequisites
        if (!embedding.IsSetup)
        {
            AnsiConsole.MarkupLine("[red]ONNX models not found. Run 'doomsummarizer setup' first.[/]");
            return 1;
        }

        embedding.Initialize();

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "DoomSummarizer/1.0");

        // Interpret the prompt if provided
        InterpretedPrompt? interpreted = null;
        var vibe = settings.Vibe;

        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[grey]Interpreting: {Markup.Escape(settings.Prompt)}[/]");

            var interpreter = new PromptInterpreter(ollama);
            interpreted = await interpreter.InterpretAsync(settings.Prompt);

            // Use interpreted vibe unless explicitly overridden
            if (settings.Vibe == "neutral" && interpreted.Vibe != "neutral")
                vibe = interpreted.Vibe;

            if (!settings.Quiet)
            {
                var sourcesStr = string.Join(", ", interpreted.Sources
                    .Concat(interpreted.Websites)
                    .Concat(interpreted.SearchQueries.Select(q => $"search:{q}")));
                AnsiConsole.MarkupLine($"[grey]Detected: sources=[[{Markup.Escape(sourcesStr)}]], vibe={vibe}[/]");
            }
        }

        // Get vibe prompt
        if (!config.Vibes.TryGetValue(vibe, out var vibePrompt))
        {
            vibePrompt = config.Vibes.GetValueOrDefault("neutral", "Objective, balanced summary.");
        }

        var ollamaAvailable = !settings.NoLlm && await ollama.IsAvailableAsync();
        if (!ollamaAvailable && !settings.Quiet)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: Ollama not available. Summaries will be limited.[/]");
        }

        var items = new List<ContentItem>();
        var uniqueItems = new List<ContentItem>();

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Stage 1: Fetch content in parallel
                var fetchTask = ctx.AddTask("[cyan]Fetching content[/]", maxValue: 100);

                var fetchTasks = new List<Task<List<ContentItem>>>();

                // Determine what to fetch
                var sources = settings.Sources?.ToList() ?? [];
                if (interpreted != null)
                {
                    sources.AddRange(interpreted.Sources);
                    sources.AddRange(interpreted.Websites);
                    sources.AddRange(interpreted.SearchQueries.Select(q => $"search:{q}"));
                }

                // If nothing specified, use defaults
                if (sources.Count == 0)
                {
                    sources.AddRange(["hn", "reddit"]);
                }

                // Dedupe sources
                sources = sources.Distinct().ToList();

                var perSourceLimit = Math.Max(10, settings.Limit / Math.Max(1, sources.Count));

                // Create parallel fetch tasks
                foreach (var source in sources)
                {
                    var src = source.ToLowerInvariant();

                    if (src == "hn")
                    {
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var fetcher = new HackerNewsFetcher(httpClient);
                            return await fetcher.FetchAsync(config.Sources.HackerNews, perSourceLimit);
                        }));
                    }
                    else if (src == "reddit" || src.StartsWith("reddit:"))
                    {
                        var subreddit = src.Contains(':') ? src.Split(':')[1] : null;
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var redditConfig = config.Sources.Reddit;
                            if (subreddit != null)
                            {
                                redditConfig = redditConfig with { Subreddits = [subreddit] };
                            }
                            var fetcher = new RedditFetcher(httpClient);
                            return await fetcher.FetchAsync(redditConfig, perSourceLimit);
                        }));
                    }
                    else if (src.StartsWith("search:"))
                    {
                        var query = source[7..]; // Keep original case for search
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var search = new DuckDuckGoSearch(httpClient);
                            return await search.SearchAsync(query, perSourceLimit);
                        }));
                    }
                    else if (src == "so" || src.StartsWith("so:"))
                    {
                        // StackOverflow: so, so:tag, so:search:query
                        var parts = src.Split(':');
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var soFetcher = new StackOverflowFetcher();
                            if (parts.Length == 1)
                            {
                                return await soFetcher.FetchHotAsync(perSourceLimit);
                            }
                            else if (parts.Length == 2)
                            {
                                return await soFetcher.FetchByTagAsync(parts[1], perSourceLimit);
                            }
                            else if (parts.Length >= 3 && parts[1] == "search")
                            {
                                var query = string.Join(":", parts[2..]);
                                return await soFetcher.SearchAsync(query, perSourceLimit);
                            }
                            return await soFetcher.FetchHotAsync(perSourceLimit);
                        }));
                    }
                    else if (NewsFetcher.KnownSources.Contains(src.Split(':')[0]))
                    {
                        // News sources: bbc, guardian, ars, verge, etc.
                        // Supports: bbc, bbc:query
                        var parts = src.Split(':');
                        var sourceName = parts[0];
                        var query = parts.Length > 1 ? string.Join(":", parts[1..]) : null;

                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var newsFetcher = new NewsFetcher(httpClient);
                            return await newsFetcher.FetchSourceAsync(sourceName, perSourceLimit, query);
                        }));
                    }
                    else if (src.StartsWith("http"))
                    {
                        var url = source; // Keep original case
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var feedDiscovery = new FeedDiscovery(httpClient);
                            var (feedItems, _) = await feedDiscovery.FetchWithDiscoveryAsync(url, perSourceLimit);

                            if (feedItems.Count > 0)
                                return feedItems;

                            // Fall back to HTML scraping
                            await using var webFetcher = new WebsiteFetcher(httpClient);
                            return await webFetcher.FetchAsync([new WebsiteConfig { Url = url }]);
                        }));
                    }
                }

                fetchTask.Value = 20;

                // Wait for all fetches in parallel
                var results = await Task.WhenAll(fetchTasks);
                foreach (var result in results)
                {
                    items.AddRange(result);
                }

                fetchTask.Value = 100;
                fetchTask.Description = $"[green]Fetched {items.Count} items[/]";

                // Apply topic filter to items from sources that don't support native filtering
                if (interpreted?.Topics.Count > 0)
                {
                    var topicTerms = interpreted.Topics.SelectMany(t => t.Split(' ')).ToList();
                    var preFilterCount = items.Count;

                    items = items.Where(item =>
                    {
                        // Keep items that match any topic term in title or content
                        var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                        return topicTerms.Any(term => text.Contains(term.ToLowerInvariant()));
                    }).ToList();

                    if (!settings.Quiet && items.Count < preFilterCount)
                        AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {items.Count} items[/]");
                }

                // Show raw content if requested
                if (settings.ShowRaw && !settings.Json)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold yellow]Raw Fetched Content:[/]");
                    foreach (var item in items.Take(settings.Limit))
                    {
                        AnsiConsole.MarkupLine($"[cyan]---[/] {Markup.Escape(item.Title)}");
                        if (!string.IsNullOrEmpty(item.Url))
                            AnsiConsole.MarkupLine($"[grey]URL:[/] {Markup.Escape(item.Url)}");
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var content = item.Content.Length > 500 ? item.Content[..500] + "..." : item.Content;
                            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(content)}[/]");
                        }
                        AnsiConsole.WriteLine();
                    }
                }

                // Load recent stored items to combine with fresh content
                var storedItems = await storage.GetRecentItemsAsync(days: 1);
                var storedContentItems = storedItems
                    .Where(s => !string.IsNullOrEmpty(s.Summary)) // Only include analyzed items
                    .Select(s => new ContentItem
                    {
                        Id = s.Id,
                        Source = s.Source,
                        Title = s.Title,
                        Url = s.Url,
                        Content = s.Summary, // Use stored summary as content
                        Summary = s.Summary,
                        DetectedTopic = s.DetectedTopic,
                        SentimentScore = s.SentimentScore,
                        Score = 0, // Lower priority than fresh items
                        CreatedAt = s.CreatedAt,
                        FetchedAt = s.FetchedAt,
                        Embedding = s.Embedding != null ? EmbeddingService.FromBytes(s.Embedding) : null
                    })
                    .ToList();

                // Combine fresh items first (higher priority), then stored items
                var allItems = items.Concat(storedContentItems).ToList();

                // Stage 2: Deduplicate by URL (not embedding - topic queries have similar content)
                var dedupeTask = ctx.AddTask("[cyan]Deduplicating[/]", maxValue: allItems.Count);
                uniqueItems.Clear(); // Use outer scope variable
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in allItems)
                {
                    // Generate embedding for analysis (skip if already has one from storage)
                    if (item.Embedding == null)
                    {
                        var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                        item.Embedding = embedding.Embed(textToEmbed);
                    }

                    // Dedupe by URL (same article from different sources)
                    // and by title (catches exact duplicates without URL)
                    var normalizedUrl = item.Url?.Split('?')[0].TrimEnd('/') ?? "";
                    var normalizedTitle = item.Title.ToLowerInvariant().Trim();

                    var isDuplicate = !string.IsNullOrEmpty(normalizedUrl) && seenUrls.Contains(normalizedUrl)
                                      || seenTitles.Contains(normalizedTitle);

                    if (!isDuplicate)
                    {
                        uniqueItems.Add(item);
                        if (!string.IsNullOrEmpty(normalizedUrl))
                            seenUrls.Add(normalizedUrl);
                        seenTitles.Add(normalizedTitle);
                    }

                    dedupeTask.Increment(1);
                }

                dedupeTask.Description = $"[green]Found {uniqueItems.Count} unique items[/]";

                // Stage 3: Signal-based analysis with salience scoring
                var analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url)>();
                var processedArticles = new List<(ProcessedArticle article, ArticleAnalysis analysis)>();

                if (ollamaAvailable)
                {
                    var analyzeTask = ctx.AddTask("[cyan]Analyzing content[/]", maxValue: Math.Min(uniqueItems.Count, settings.Limit));

                    // Always use signal-based processing for best quality
                    using var articleProcessor = new ArticleProcessor();

                    foreach (var item in uniqueItems.Take(settings.Limit))
                    {
                        try
                        {
                            // Signal-based processing: extract segments with salience scoring
                            var processed = await articleProcessor.ProcessAsync(item);

                            // Use signal-aware analysis with top segments
                            var analysis = await ollama.AnalyzeProcessedArticleAsync(
                                processed, vibePrompt, includeReferences: false);

                            item.Summary = analysis.Summary;
                            item.DetectedTopic = analysis.Topic;
                            item.SentimentScore = analysis.Sentiment;

                            analyzedItems.Add((item.Title, analysis.Summary, analysis.Topic, analysis.Sentiment, item.Url ?? ""));
                            processedArticles.Add((processed, analysis));
                        }
                        catch
                        {
                            item.Summary = item.Title;
                            item.DetectedTopic = "general";
                            analyzedItems.Add((item.Title, item.Title, "general", 0, item.Url ?? ""));
                        }

                        // Save to storage
                        await storage.SaveItemAsync(item);
                        analyzeTask.Increment(1);
                    }

                    analyzeTask.Description = $"[green]Analyzed {analyzedItems.Count} items[/]";
                }
                else
                {
                    // No LLM - use salience-based segment extraction for better results
                    using var articleProcessor = new ArticleProcessor();

                    foreach (var item in uniqueItems.Take(settings.Limit))
                    {
                        var processed = await articleProcessor.ProcessAsync(item);

                        // Use top segment text as summary
                        var topSegment = processed.TopSegments.FirstOrDefault();
                        item.Summary = topSegment?.Text ?? item.Title;
                        item.DetectedTopic = "general";
                        analyzedItems.Add((item.Title, item.Summary, "general", 0, item.Url ?? ""));
                        await storage.SaveItemAsync(item);
                    }
                }

                // Deduplicate analyzed items by URL to prevent duplicate entries in summary
                analyzedItems = analyzedItems
                    .GroupBy(i => i.url)
                    .Select(g => g.First())
                    .ToList();

                // NER entity extraction if requested
                var allEntities = new List<NerEntity>();
                if (settings.ShowEntities)
                {
                    var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: analyzedItems.Count);
                    using var nerService = new NerService();

                    if (nerService.IsAvailable)
                    {
                        await nerService.InitializeAsync();
                        foreach (var item in analyzedItems)
                        {
                            var textForNer = $"{item.title} {item.summary}";
                            var entities = await nerService.ExtractEntitiesAsync(textForNer);
                            allEntities.AddRange(entities);
                            nerTask.Increment(1);
                        }
                        // Dedupe entities
                        allEntities = allEntities
                            .GroupBy(e => e.Text.ToLowerInvariant())
                            .Select(g => g.OrderByDescending(e => e.Confidence).First())
                            .OrderByDescending(e => e.Confidence)
                            .ToList();
                    }
                    nerTask.Description = $"[green]Found {allEntities.Count} entities[/]";
                }

                // Stage 4: Generate summary
                var summaryTask = ctx.AddTask("[cyan]Generating summary[/]", maxValue: 100);

                string finalSummary;
                if (ollamaAvailable && analyzedItems.Count > 0)
                {
                    summaryTask.Value = 50;
                    // Pass raw prompt so LLM can answer the user's actual question
                    var userQuery = interpreted?.RawPrompt ?? settings.Prompt;
                    finalSummary = await ollama.SynthesizeSummaryAsync(analyzedItems, vibe, vibePrompt, userQuery);
                }
                else
                {
                    finalSummary = GenerateFallbackSummary(analyzedItems, vibe);
                }

                summaryTask.Value = 100;
                summaryTask.Description = "[green]Summary generated[/]";

                // Save summary
                await storage.SaveSummaryAsync(vibe, finalSummary, analyzedItems.Count);

                // Output
                if (settings.Json)
                {
                    // Machine-readable JSON output for LLM tool consumption
                    var jsonOutput = new
                    {
                        vibe,
                        generated = DateTimeOffset.UtcNow,
                        itemCount = analyzedItems.Count,
                        summary = finalSummary,
                        items = analyzedItems.Select(i => new
                        {
                            title = i.title,
                            summary = i.summary,
                            topic = i.topic,
                            sentiment = i.sentiment,
                            url = i.url
                        }).ToArray(),
                        entities = settings.ShowEntities ? allEntities.Select(e => new
                        {
                            text = e.Text,
                            type = e.Type,
                            confidence = e.Confidence
                        }).ToArray() : null
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(jsonOutput,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                    if (!string.IsNullOrEmpty(settings.Output))
                    {
                        await File.WriteAllTextAsync(settings.Output, json);
                        if (!settings.Quiet)
                            AnsiConsole.MarkupLine($"[green]JSON saved to:[/] {settings.Output}");
                    }
                    else
                    {
                        Console.WriteLine(json);
                    }
                }
                else if (!string.IsNullOrEmpty(settings.Output))
                {
                    await File.WriteAllTextAsync(settings.Output, finalSummary);
                    AnsiConsole.MarkupLine($"[green]Summary saved to:[/] {settings.Output}");
                }
                else
                {
                    AnsiConsole.WriteLine();
                    var escapedSummary = Markup.Escape(finalSummary);
                    AnsiConsole.Write(new Panel(escapedSummary)
                        .Header($"[bold cyan]Doom Scroll Digest ({vibe})[/]")
                        .Border(BoxBorder.Rounded)
                        .Padding(1, 1));

                    // Display entities if requested
                    if (settings.ShowEntities && allEntities.Count > 0)
                    {
                        AnsiConsole.WriteLine();
                        var entityTable = new Table()
                            .Title("[bold yellow]Named Entities[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]Entity[/]")
                            .AddColumn("[cyan]Type[/]")
                            .AddColumn("[cyan]Confidence[/]");

                        foreach (var entity in allEntities.Take(20))
                        {
                            var typeColor = entity.Type switch
                            {
                                "PER" => "green",
                                "ORG" => "blue",
                                "LOC" => "yellow",
                                _ => "grey"
                            };
                            entityTable.AddRow(
                                Markup.Escape(entity.Text),
                                $"[{typeColor}]{entity.Type}[/]",
                                $"{entity.Confidence:P0}");
                        }
                        AnsiConsole.Write(entityTable);
                    }

                    // Display images for important items
                    if (settings.ShowImages)
                    {
                        // Get items with images, sorted by importance (score)
                        var itemsWithImages = uniqueItems
                            .Where(i => !string.IsNullOrEmpty(i.ImageUrl))
                            .OrderByDescending(i => i.Score)
                            .Take(3)
                            .ToList();

                        // Also try to fetch og:image for high-scoring items without images
                        var highScoringWithoutImages = uniqueItems
                            .Where(i => string.IsNullOrEmpty(i.ImageUrl) && i.Score > 50 && !string.IsNullOrEmpty(i.Url))
                            .OrderByDescending(i => i.Score)
                            .Take(2);

                        using var imageService = new ImageService(httpClient);

                        foreach (var item in highScoringWithoutImages)
                        {
                            var ogImage = await imageService.FetchOgImageAsync(item.Url!);
                            if (!string.IsNullOrEmpty(ogImage))
                            {
                                item.ImageUrl = ogImage;
                                itemsWithImages.Add(item);
                            }
                        }

                        if (itemsWithImages.Count > 0)
                        {
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[bold yellow]Featured Images[/]");
                            AnsiConsole.WriteLine();

                            foreach (var item in itemsWithImages.Take(3))
                            {
                                var localPath = await imageService.DownloadImageAsync(item.ImageUrl!, item.Id);
                                if (localPath != null)
                                {
                                    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(item.Title)}[/]");
                                    if (item.Score > 0)
                                        AnsiConsole.MarkupLine($"[grey]Score: {item.Score}[/]");
                                    imageService.DisplayImage(localPath, maxWidth: 50);
                                    AnsiConsole.WriteLine();
                                }
                            }
                        }
                    }
                }

                // Cleanup old data
                await storage.CleanupOldDataAsync(config.Storage.RetentionDays);
            });

        return 0;
    }

    private static string GenerateFallbackSummary(
        List<(string title, string summary, string topic, float sentiment, string url)> items,
        string vibe)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Doom Scroll Digest ({vibe})");
        sb.AppendLine();
        sb.AppendLine($"*Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm}*");
        sb.AppendLine();

        var byTopic = items.GroupBy(x => x.topic).OrderByDescending(g => g.Count());

        foreach (var group in byTopic)
        {
            sb.AppendLine($"## {group.Key}");
            foreach (var item in group.Take(5))
            {
                if (!string.IsNullOrEmpty(item.url))
                    sb.AppendLine($"- [{item.title}]({item.url})");
                else
                    sb.AppendLine($"- {item.title}");

                // Show extracted segment if different from title (signal-based content)
                if (!string.IsNullOrEmpty(item.summary) && item.summary != item.title)
                {
                    var truncated = item.summary.Length > 200
                        ? item.summary[..200] + "..."
                        : item.summary;
                    sb.AppendLine($"  > {truncated}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

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
        [Description("Sentiment steering: doom, hopeful, snarky, neutral, or any custom text (e.g., 'excited about space')")]
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

        [CommandOption("--no-llm|--nollm")]
        [Description("Skip LLM analysis — still runs embeddings, BM25, sentiment, topic inference")]
        public bool NoLlm { get; init; }

        [CommandOption("--json")]
        [Description("Output as JSON (for LLM tool consumption)")]
        public bool Json { get; init; }

        [CommandOption("--entities")]
        [Description("Enable NER entity extraction")]
        public bool Entities { get; init; }

        [CommandOption("--graph")]
        [Description("Enable knowledge graph build and display")]
        public bool Graph { get; init; }

        [CommandOption("--no-links")]
        [Description("Skip one-hop link following")]
        public bool NoLinks { get; init; }

        [CommandOption("--raw")]
        [Description("Show raw fetched content before processing")]
        public bool ShowRaw { get; init; }

        [CommandOption("--images")]
        [Description("Display inline images for important items")]
        public bool ShowImages { get; init; }

        [CommandOption("--local")]
        [Description("Query ONLY the local knowledge base — no fetching, uses previously stored articles")]
        public bool LocalOnly { get; init; }

        [CommandOption("--debug-pipeline|--debug")]
        [Description("Show detailed pipeline diagnostics: RRF component scores, discards, salience breakdown")]
        public bool DebugPipeline { get; init; }

        [CommandOption("--list-templates")]
        [Description("List available output templates")]
        public bool ListTemplates { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Handle --list-templates
        if (settings.ListTemplates)
        {
            var templateService = new TemplateService();
            await templateService.LoadCustomTemplatesAsync(
                Path.Combine(ConfigService.GetConfigDir(), "templates"));

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
            table.AddRow("[bold]blog-article[/]", "[cyan]Multi-section long-form article (auto-detects timeline)[/]");
            table.AddRow("[bold]blog-timeline[/]", "[cyan]Chronological article with timeline structure[/]");
            table.AddRow("[bold]blog-newsletter[/]", "[cyan]Curated newsletter with editorial picks[/]");
            table.AddRow("[bold]blog-newsletter-html[/]", "[cyan]Newsletter as styled HTML email[/]");

            // Show YAML-defined templates
            foreach (var name in templateService.ListDefinitions())
            {
                var def = templateService.GetDefinition(name);
                var desc = def?.Description ?? "Custom YAML template";
                var sections = def?.HasFixedSections == true
                    ? $" ({def.Sections.Count} sections)"
                    : "";
                table.AddRow($"[bold yellow]{Markup.Escape(name)}[/]",
                    $"[yellow]{Markup.Escape(desc)}{sections}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("\n[grey]Custom templates: place .liquid or .yaml files in ~/.doomsummarizer/templates/[/]");
            return 0;
        }

        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        await using var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        // Initialize DuckDB vector store for HNSW-backed knowledge graph (only when --graph is requested)
        DuckDbVectorStore? vectorStore = null;
        if (settings.Graph)
        {
            var vectorDbPath = ConfigService.GetVectorDbPath();
            vectorStore = new DuckDbVectorStore(vectorDbPath);
            await vectorStore.InitializeAsync();
        }

        // Initialize template service for output rendering
        var outputTemplates = new TemplateService();
        var templatesDir = Path.Combine(ConfigService.GetConfigDir(), "templates");
        await outputTemplates.LoadCustomTemplatesAsync(templatesDir);

        using var embedding = new EmbeddingService();
        var ollama = new OllamaService(config.Ollama);

        // Initialize API key service and budget tracker for paid APIs
        var apiKeys = ApiKeyService.Load(config);
        await using var apiBudget = new ApiBudgetService(config.ApiBudget, apiKeys, dbPath);
        await apiBudget.InitializeAsync();

        // Wire cloud LLM providers (OpenAI/Anthropic) through the router
        // When available, OllamaService delegates generate calls through the router
        // with budget enforcement and automatic fallback to local Ollama
        var llmRouter = LlmRouter.Build(config.Ollama, apiKeys, apiBudget);
        ollama.Router = llmRouter;

        // Auto-setup: download ONNX models if not present (first run)
        await embedding.EnsureReadyAsync(msg =>
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
        });

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

        // NER preprocessing: extract entities from query BEFORE the LLM sentinel
        // This gives us structured search filters, cached segment lookups, and URL dedup
        QueryNerContext? nerContext = null;
        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            nerContext = await QueryPreprocessor.PreprocessAsync(
                settings.Prompt, embedding, storage, cancellationToken);

            if (!settings.Quiet && nerContext.HasEntities)
            {
                var entityStr = string.Join(", ", nerContext.Entities
                    .Select(e => $"{e.Text} ({e.Type})"));
                AnsiConsole.MarkupLine($"[grey]NER: {Markup.Escape(entityStr)}[/]");
                if (nerContext.HasCachedData)
                    AnsiConsole.MarkupLine($"[grey]Cache: {nerContext.CachedItems.Count} matching segments, {nerContext.KnownUrls.Count} known URLs[/]");
            }
        }

        // Interpret the prompt if provided
        InterpretedPrompt? interpreted = null;
        var vibe = settings.Vibe;

        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[grey]Interpreting: {Markup.Escape(settings.Prompt)}[/]");

            var interpreter = new PromptInterpreter(ollama, embedding);
            interpreted = await interpreter.InterpretAsync(settings.Prompt, nerContext);

            // Use interpreted vibe unless explicitly overridden
            if (settings.Vibe == "neutral" && interpreted.Vibe != "neutral")
                vibe = interpreted.Vibe;

            if (!settings.Quiet)
            {
                var sourcesStr = string.Join(", ", interpreted.Sources
                    .Concat(interpreted.Websites)
                    .Concat(interpreted.SearchQueries.Select(q => $"search:{q}")));
                AnsiConsole.MarkupLine($"[grey]Detected: sources=[[{Markup.Escape(sourcesStr)}]], vibe={vibe}[/]");

                // Show sentinel intent details for debugging routing decisions
                if (interpreted.SentinelIntent is { } si)
                {
                    var cats = string.Join(", ", si.Categories.Select(c => $"{c.Key}={c.Value:F1}"));
                    var queries = string.Join(", ", si.SearchQueries);
                    AnsiConsole.MarkupLine($"[grey]Sentinel: intent={Markup.Escape(si.Intent)}, cats={Markup.Escape(cats)}, queries={Markup.Escape(queries)}[/]");
                }
            }
        }

        // Get vibe prompt - supports predefined vibes or arbitrary text
        string vibePrompt;
        if (config.Vibes.TryGetValue(vibe, out var configuredPrompt))
        {
            vibePrompt = configuredPrompt;
        }
        else if (IsCustomVibe(vibe))
        {
            // Arbitrary vibe text - use it directly as the instruction
            vibePrompt = $"Apply this tone/perspective: {vibe}. Filter and present content through this lens.";
        }
        else
        {
            vibePrompt = config.Vibes.GetValueOrDefault("neutral", "Objective, balanced summary.");
        }

        var ollamaAvailable = !settings.NoLlm && await ollama.IsAvailableAsync();
        if (!ollamaAvailable && !settings.Quiet)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: Ollama not available. Summaries will be limited.[/]");
        }

        // Query feedback: check for similar recent query to reuse cached segments
        var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";
        float[]? earlyQueryEmbedding = null;
        QueryMatch? cachedQuery = null;
        var useCachedSegments = false;

        if (!settings.Force && !settings.LocalOnly && !string.IsNullOrWhiteSpace(queryText))
        {
            earlyQueryEmbedding = embedding.Embed(queryText);
            cachedQuery = await storage.FindSimilarQueryAsync(earlyQueryEmbedding, threshold: 0.97);
            if (cachedQuery != null)
            {
                useCachedSegments = true;
                if (!settings.Quiet)
                {
                    var ageMin = (int)(DateTimeOffset.UtcNow - cachedQuery.IssuedAt).TotalMinutes;
                    AnsiConsole.MarkupLine($"[grey]Reusing {cachedQuery.ItemIds.Count} segments from similar query ({cachedQuery.Similarity:F2} match, {ageMin}m ago): \"{Markup.Escape(cachedQuery.QueryText)}\"[/]");
                    if (cachedQuery.Vibe != vibe)
                        AnsiConsole.MarkupLine($"[grey]Different vibe ({cachedQuery.Vibe} → {vibe}): will regenerate synthesis[/]");
                }
            }
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
                // Stage 1: Fetch content (or load from knowledge base)
                var fetchTask = ctx.AddTask(
                    settings.LocalOnly ? "[cyan]Loading from knowledge base[/]" : "[cyan]Fetching content[/]",
                    maxValue: 100);

                // --local mode: skip ALL fetching, query stored knowledge base only
                if (settings.LocalOnly)
                {
                    var localQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";

                    // Check for source filter (e.g. --source crawl:mysite for KB queries)
                    var sourceFilter = settings.Sources?.FirstOrDefault(s =>
                        s.StartsWith("crawl:", StringComparison.OrdinalIgnoreCase));
                    var storedLocal = sourceFilter != null
                        ? await storage.GetRecentItemsAsync(days: 365, source: sourceFilter)
                        : await storage.GetRecentItemsAsync(days: 30);
                    fetchTask.Value = 40;

                    // Convert stored items to ContentItems
                    var localItems = storedLocal
                        .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                        .Select(s => s.ToContentItem())
                        .ToList();

                    // If we have a query and embeddings, do semantic search to filter
                    if (!string.IsNullOrWhiteSpace(localQuery) && localItems.Any(i => i.Embedding != null))
                    {
                        var queryEmbed = embedding.Embed(localQuery);
                        // Score by embedding similarity to the query
                        foreach (var item in localItems)
                        {
                            if (item.Embedding != null)
                            {
                                var sim = EmbeddingService.CosineSimilarity(queryEmbed, item.Embedding);
                                item.RelevanceScore = sim;
                            }
                        }
                        // Keep items with reasonable similarity
                        localItems = localItems
                            .Where(i => i.RelevanceScore > 0.2)
                            .OrderByDescending(i => i.RelevanceScore)
                            .Take(settings.Limit)
                            .ToList();
                    }
                    else
                    {
                        // No query: return most recent
                        localItems = localItems
                            .OrderByDescending(i => i.FetchedAt)
                            .Take(settings.Limit)
                            .ToList();
                    }

                    items.AddRange(localItems);
                    fetchTask.Value = 100;
                    fetchTask.Description = $"[green]Loaded {items.Count} items from knowledge base ({storedLocal.Count} total stored)[/]";

                    if (!settings.Quiet)
                        AnsiConsole.MarkupLine($"[grey]Local mode: {storedLocal.Count} stored items, {items.Count} matched query[/]");
                }

                // Segment reuse: load cached items from a similar recent query
                if (!settings.LocalOnly && useCachedSegments && cachedQuery != null)
                {
                    var cachedStored = await storage.GetItemsByIdsAsync(cachedQuery.ItemIds);
                    var cachedItems = cachedStored
                        .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                        .Select(s => s.ToContentItem())
                        .ToList();

                    items.AddRange(cachedItems);
                    fetchTask.Value = 100;
                    fetchTask.Description = $"[green]Reused {items.Count} cached segments (skipped fetching)[/]";
                }

                if (!settings.LocalOnly && !useCachedSegments)
                {
                // Normal fetch mode
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
                    else if (src.StartsWith("gnews_topic:"))
                    {
                        // Google News topic feed (HEALTH, SCIENCE, BUSINESS, etc.)
                        var topic = source[12..];
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var gnews = new GoogleNewsFetcher(httpClient);
                            return await gnews.FetchTopicAsync(topic, perSourceLimit);
                        }));
                    }
                    else if (src.StartsWith("gnews:") || src == "gnews")
                    {
                        // Google News RSS search
                        var query = src == "gnews" ? interpreted?.RawPrompt ?? "" : source[6..];
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var gnews = new GoogleNewsFetcher(httpClient);
                            return await gnews.SearchAsync(qualifiedQuery, perSourceLimit, daysBack: 7);
                        }));
                    }
                    else if (src.StartsWith("search:"))
                    {
                        var query = source[7..]; // Keep original case for search
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        var searchLimit = perSourceLimit * 2;

                        // Use the best available search API (priority order)
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            if (apiKeys.HasGoogleSearch)
                                return await new GoogleSearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            if (apiKeys.IsAvailable("brave_search"))
                                return await new BraveSearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            if (apiKeys.IsAvailable("serper"))
                                return await new SerperSearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            if (apiKeys.IsAvailable("tavily"))
                                return await new TavilySearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            // DDG as last resort
                            return await new DuckDuckGoSearch(httpClient)
                                .SearchAsync(qualifiedQuery, searchLimit);
                        }));
                    }
                    else if (src.StartsWith("gsearch:") || src == "gsearch")
                    {
                        var query = src == "gsearch"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[8..];
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        fetchTasks.Add(Task.Run(async () =>
                            await new GoogleSearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(qualifiedQuery, perSourceLimit * 2)));
                    }
                    else if (src.StartsWith("gplaces:") || src == "gplaces")
                    {
                        var query = src == "gplaces"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[8..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new GooglePlacesService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("brave:") || src == "brave")
                    {
                        var query = src == "brave"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[6..];
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        fetchTasks.Add(Task.Run(async () =>
                            await new BraveSearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(qualifiedQuery, perSourceLimit * 2)));
                    }
                    else if (src.StartsWith("bravenews:") || src == "bravenews")
                    {
                        var query = src == "bravenews"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[10..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new BraveSearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit, newsOnly: true)));
                    }
                    else if (src.StartsWith("serper:") || src == "serper")
                    {
                        var query = src == "serper"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[7..];
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        fetchTasks.Add(Task.Run(async () =>
                            await new SerperSearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(qualifiedQuery, perSourceLimit * 2)));
                    }
                    else if (src.StartsWith("serpernews:") || src == "serpernews")
                    {
                        var query = src == "serpernews"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[11..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new SerperSearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit, newsOnly: true)));
                    }
                    else if (src.StartsWith("tavily:") || src == "tavily")
                    {
                        var query = src == "tavily"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[7..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new TavilySearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("newsapi:") || src == "newsapi")
                    {
                        var query = src == "newsapi"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[8..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new NewsApiService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("newsdata:") || src == "newsdata")
                    {
                        var query = src == "newsdata"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[9..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new NewsDataService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("jina:") || src == "jina")
                    {
                        var query = src == "jina"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[5..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new JinaSearchService(httpClient, apiKeys, apiBudget)
                                .SearchAsync(query, perSourceLimit)));
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
                    else if (src == "factcheck" || src.StartsWith("factcheck:"))
                    {
                        // Fact-checking: factcheck, factcheck:snopes, factcheck:politifact
                        var site = src.Contains(':') ? src.Split(':')[1] : null;
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var fetcher = new FactCheckFetcher(httpClient);
                            return await fetcher.FetchAsync(perSourceLimit, site);
                        }));
                    }
                    else if (src == "spaceflight" || src == "space")
                    {
                        // Spaceflight News API: spaceflight, space
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var fetcher = new SpaceflightNewsFetcher(httpClient);
                            return await fetcher.FetchAsync(perSourceLimit);
                        }));
                    }
                    else if (src == "earthquake" || src == "quake" || src.StartsWith("earthquake:"))
                    {
                        // USGS Earthquakes: earthquake, earthquake:significant_month, earthquake:4.5_week
                        var feed = src.Contains(':') ? src.Split(':')[1] : null;
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var fetcher = new UsgsEarthquakeFetcher(httpClient);
                            return await fetcher.FetchAsync(perSourceLimit, feed);
                        }));
                    }
                    else if (src == "wikipedia" || src == "wiki" || src.StartsWith("wiki:"))
                    {
                        // Wikipedia current events: wiki, wiki:news, wiki:history, wiki:featured
                        var section = src.Contains(':') ? src.Split(':')[1] : null;
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var fetcher = new WikipediaFetcher(httpClient);
                            return await fetcher.FetchAsync(perSourceLimit, section);
                        }));
                    }
                    else if (src == "arxiv" || src.StartsWith("arxiv:"))
                    {
                        // arXiv papers: arxiv, arxiv:query, arxiv:cat:cs.AI
                        var parts = src.Split(':');
                        fetchTasks.Add(Task.Run(async () =>
                        {
                            var fetcher = new ArxivFetcher(httpClient);
                            if (parts.Length >= 3 && parts[1] == "cat")
                            {
                                // Category browse: arxiv:cat:cs.AI
                                return await fetcher.FetchCategoryAsync(parts[2], perSourceLimit);
                            }
                            else if (parts.Length >= 2)
                            {
                                // Search: arxiv:query terms
                                var query = string.Join(":", parts[1..]);
                                return await fetcher.SearchAsync(query, perSourceLimit);
                            }
                            else
                            {
                                // Default: search with interpreted prompt
                                var query = interpreted?.RawPrompt ?? settings.Prompt ?? "recent";
                                return await fetcher.SearchAsync(query, perSourceLimit);
                            }
                        }));
                    }
                    else if (NewsFetcher.KnownSources.Contains(src.Split(':')[0]))
                    {
                        // News sources: bbc, guardian, ars, verge, etc.
                        // Supports: bbc, bbc:category, bbc:query
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

                fetchTask.Value = 80;

                // Source diversity fallback: if initial fetch returned too few items,
                // auto-add search fallbacks to fill the gap
                var minDesired = Math.Max(15, settings.Limit / 2);
                if (items.Count < minDesired && !string.IsNullOrEmpty(interpreted?.RawPrompt ?? settings.Prompt))
                {
                    var fallbackQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                    var fallbackSources = new List<Task<List<ContentItem>>>();

                    // Use available search APIs as fallback (cascade through services)
                    var hasSearchSource = sources.Any(s =>
                        s.StartsWith("search:", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("gsearch", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("brave", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("serper", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("tavily", StringComparison.OrdinalIgnoreCase));

                    if (!hasSearchSource)
                    {
                        // Pick the best available search API
                        if (apiKeys.IsAvailable("brave_search"))
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new BraveSearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(fallbackQuery, perSourceLimit * 2)));
                        }
                        else if (apiKeys.IsAvailable("serper"))
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new SerperSearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(fallbackQuery, perSourceLimit * 2)));
                        }
                        else if (apiKeys.IsAvailable("tavily"))
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new TavilySearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(fallbackQuery, perSourceLimit)));
                        }
                        else if (apiKeys.HasGoogleSearch)
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new GoogleSearchService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(fallbackQuery, perSourceLimit * 2)));
                        }
                        else
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new DuckDuckGoSearch(httpClient)
                                    .SearchAsync(fallbackQuery, perSourceLimit * 2)));
                        }
                    }

                    // Add news fallbacks if not already present
                    var hasNewsSource = sources.Any(s =>
                        s.StartsWith("gnews", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("newsapi", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("newsdata", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("bravenews", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("serpernews", StringComparison.OrdinalIgnoreCase));

                    if (!hasNewsSource)
                    {
                        // Use news APIs in parallel for diversity
                        if (apiKeys.IsAvailable("newsapi"))
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new NewsApiService(httpClient, apiKeys, apiBudget)
                                    .SearchAsync(fallbackQuery, perSourceLimit)));
                        }
                        else
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new GoogleNewsFetcher(httpClient)
                                    .SearchAsync(fallbackQuery, perSourceLimit, daysBack: 7)));
                        }
                    }

                    if (fallbackSources.Count > 0)
                    {
                        var fallbackResults = await Task.WhenAll(fallbackSources);
                        var fallbackCount = 0;
                        foreach (var fb in fallbackResults)
                        {
                            items.AddRange(fb);
                            fallbackCount += fb.Count;
                        }
                        if (!settings.Quiet && fallbackCount > 0)
                            AnsiConsole.MarkupLine($"[grey]Diversity fallback: added {fallbackCount} items from backup sources[/]");
                    }
                }

                fetchTask.Value = 100;
                fetchTask.Description = $"[green]Fetched {items.Count} items[/]";

                // Apply topic filter ONLY to items from generic sources (hn, reddit, lobsters, devto)
                // that don't natively filter by topic. Skip for search/gnews/category-specific feeds
                // since those already fetched topic-relevant content.
                if (interpreted?.Topics.Count > 0)
                {
                    var topicTerms = interpreted.Topics.SelectMany(t => t.Split(' ')).ToList();
                    // Sources that already searched/filtered for the topic
                    var topicAwareSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "gnews", "search", "bbc", "guardian", "cnn", "reuters",
                          "factcheck", "spaceflight", "earthquake", "wikipedia", "arxiv" };

                    var preFilterCount = items.Count;

                    var filtered = items.Where(item =>
                    {
                        // Keep all items from topic-aware sources (they already filtered)
                        if (topicAwareSources.Contains(item.Source))
                            return true;

                        // Filter generic sources by topic terms
                        var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                        return topicTerms.Any(term => text.Contains(term.ToLowerInvariant()));
                    }).ToList();

                    // Graceful fallback: if topic filter is too aggressive (< 5 items),
                    // keep all topic-aware items + relax to allow partial term matches
                    if (filtered.Count < 5 && preFilterCount >= 5)
                    {
                        // Softer filter: any single word from topic terms (not full phrases)
                        var singleWords = topicTerms
                            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            .Where(w => w.Length > 3)
                            .Select(w => w.ToLowerInvariant())
                            .Distinct()
                            .ToList();

                        filtered = items.Where(item =>
                        {
                            if (topicAwareSources.Contains(item.Source))
                                return true;
                            var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                            return singleWords.Any(word => text.Contains(word));
                        }).ToList();

                        // If still too few, skip topic filter entirely (rely on downstream relevance)
                        if (filtered.Count < 5)
                        {
                            if (!settings.Quiet)
                                AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} (too aggressive, skipping)[/]");
                            filtered = items;
                        }
                        else if (!settings.Quiet)
                        {
                            AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items (relaxed)[/]");
                        }
                    }
                    else if (!settings.Quiet && filtered.Count < preFilterCount)
                    {
                        AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items[/]");
                    }

                    items = filtered;
                }

                // Roundup intent: date-gate and penalize topic drift
                var earlyQueryType = QueryTypeDetector.Detect(interpreted?.RawPrompt ?? settings.Prompt, interpreted?.SentinelIntent);
                if (earlyQueryType == QueryType.Roundup)
                {
                    var preRoundupCount = items.Count;

                    // Penalize "on this day" / historical drift content
                    foreach (var item in items)
                    {
                        if (QueryTypeDetector.IsTopicDrift(item))
                            item.RelevanceScore *= 0.3; // Heavy penalty
                    }

                    // Date-gate: if user said "today", strongly prefer last 48h
                    if (QueryTypeDetector.ImpliesDateGating(interpreted?.RawPrompt ?? settings.Prompt))
                    {
                        var maxAge = TimeSpan.FromHours(48);
                        foreach (var item in items)
                        {
                            var mult = QueryTypeDetector.GetFreshnessMultiplier(item, maxAge);
                            item.RelevanceScore *= mult;
                        }

                        // Re-sort by relevance after freshness adjustment
                        items = items.OrderByDescending(i => i.RelevanceScore).ToList();

                        if (!settings.Quiet)
                        {
                            var freshCount = items.Count(i =>
                                (DateTimeOffset.UtcNow - i.CreatedAt) <= maxAge);
                            AnsiConsole.MarkupLine($"[grey]Roundup date-gate: {freshCount}/{items.Count} items within 48h[/]");
                        }
                    }
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

                // Load recent stored items to combine with fresh content (skip with --force)
                var storedItems = settings.Force ? [] : await storage.GetRecentItemsAsync(days: 1);
                var storedContentItems = storedItems
                    .Where(s => !string.IsNullOrEmpty(s.Summary)) // Only include analyzed items
                    .Select(s => s.ToContentItem() with { Score = 0 }) // Lower priority than fresh items
                    .ToList();

                // Combine fresh items first (higher priority), then stored items
                items.AddRange(storedContentItems);

                // Inject NER-matched cached items (entity-specific, high relevance)
                if (nerContext?.HasCachedData == true)
                {
                    var existingUrls = new HashSet<string>(
                        items.Where(i => !string.IsNullOrEmpty(i.Url)).Select(i => i.Url!),
                        StringComparer.OrdinalIgnoreCase);

                    var nerCachedItems = nerContext.CachedItems
                        .Where(s => !string.IsNullOrEmpty(s.Summary))
                        .Select(s => s.ToContentItem())
                        .Where(c => !existingUrls.Contains(c.Url ?? ""))
                        .ToList();

                    if (nerCachedItems.Count > 0)
                    {
                        items.AddRange(nerCachedItems);
                        if (!settings.Quiet)
                            AnsiConsole.MarkupLine($"[grey]NER cache: injected {nerCachedItems.Count} entity-matched items[/]");
                    }
                }
                } // end normal fetch mode

                // Stage 2: Deduplicate by URL (not embedding - topic queries have similar content)
                var dedupeTask = ctx.AddTask("[cyan]Deduplicating[/]", maxValue: items.Count);
                uniqueItems.Clear(); // Use outer scope variable
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
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

                // Stage 2.1: Source domain filtering (allow/block lists)
                if (config.SourceFilter.AllowedDomains.Count > 0 || config.SourceFilter.BlockedDomains.Count > 0)
                {
                    var preFilterCount = uniqueItems.Count;
                    uniqueItems = ApplySourceDomainFilter(uniqueItems, config.SourceFilter);

                    if (!settings.Quiet && uniqueItems.Count < preFilterCount)
                        AnsiConsole.MarkupLine($"[grey]Source filter: {preFilterCount} → {uniqueItems.Count} items[/]");
                }

                // Stage 2.5: Embedding computation + two-phase relevance scoring with RRF
                var scorer = new RelevanceScorer();
                var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";

                // Compute embeddings for ALL items BEFORE scoring
                // This enables semantic matching in Phase 1 (e.g. "pharmaceutical" matches "drug pricing")
                // without needing synonym dictionaries — embeddings capture semantic similarity dynamically
                float[]? queryEmbedding = null;
                float[]? vibeEmbedding = null;
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    foreach (var item in uniqueItems)
                    {
                        if (item.Embedding == null)
                        {
                            var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                            item.Embedding = embedding.Embed(textToEmbed);
                        }
                    }

                    queryEmbedding = embedding.Embed(queryText);
                    var vibeText = GetVibeRepresentativeText(vibe);
                    vibeEmbedding = vibe != "neutral" ? embedding.Embed(vibeText) : null;
                }

                // Phase 1: Fast discard using BM25 + freshness + authority + semantic similarity
                if (!string.IsNullOrWhiteSpace(queryText) && uniqueItems.Count > 5)
                {
                    var preScoreCount = uniqueItems.Count;

                    // Capture pre-discard scores for debug output
                    List<(ContentItem item, double bm25, double freshness, double authority, double qSim)>? phase1Debug = null;
                    if (settings.DebugPipeline)
                    {
                        var qt = RelevanceScorer.Tokenize(queryText);
                        var (idf, avgDocLen) = RelevanceScorer.BuildCorpusStats(uniqueItems);
                        phase1Debug = uniqueItems.Select(i => (
                            item: i,
                            bm25: RelevanceScorer.BM25Score(RelevanceScorer.ItemText(i), qt, idf, avgDocLen),
                            freshness: RelevanceScorer.ComputeFreshness(i),
                            authority: RelevanceScorer.NormalizeAuthority(i, uniqueItems),
                            qSim: i.Embedding != null && queryEmbedding != null
                                ? (double)EmbeddingService.CosineSimilarity(i.Embedding, queryEmbedding) : 0.0
                        )).ToList();
                    }

                    uniqueItems = scorer.ScoreFast(uniqueItems, queryText, discardRatio: 0.25, queryEmbedding: queryEmbedding);

                    if (settings.DebugPipeline && phase1Debug != null)
                    {
                        // Show which items were kept vs discarded
                        var keptIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                        AnsiConsole.WriteLine();
                        var table = new Table()
                            .Title("[bold yellow]Phase 1: Scoring (BM25 + Freshness + Authority + Semantic)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]Status[/]")
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]BM25[/]")
                            .AddColumn("[cyan]Fresh[/]")
                            .AddColumn("[cyan]Auth[/]")
                            .AddColumn("[cyan]QSim[/]")
                            .AddColumn("[cyan]RRF[/]")
                            .AddColumn("[cyan]Title[/]");

                        foreach (var d in phase1Debug.OrderByDescending(x => x.item.RelevanceScore).Take(30))
                        {
                            var kept = keptIds.Contains(d.item.Id);
                            var status = kept ? "[green]KEPT[/]" : "[red]CUT[/]";
                            table.AddRow(
                                status,
                                Markup.Escape(d.item.Source),
                                $"{d.bm25:F2}",
                                $"{d.freshness:F2}",
                                $"{d.authority:F2}",
                                $"{d.qSim:F3}",
                                $"{d.item.RelevanceScore:F3}",
                                Markup.Escape(d.item.Title.Length > 60 ? d.item.Title[..57] + "..." : d.item.Title));
                        }
                        AnsiConsole.Write(table);
                        AnsiConsole.MarkupLine($"[grey]Query tokens: {string.Join(", ", RelevanceScorer.Tokenize(queryText))}[/]");
                    }

                    if (!settings.Quiet && uniqueItems.Count < preScoreCount)
                        AnsiConsole.MarkupLine($"[grey]Fast relevance filter: {preScoreCount} → {uniqueItems.Count} items (discarded low-salience)[/]");
                }

                // Phase 2: Full RRF with vibe alignment added (embeddings already computed)
                if (!string.IsNullOrWhiteSpace(queryText) && queryEmbedding != null)
                {
                    uniqueItems = scorer.ScoreFull(uniqueItems, queryText, queryEmbedding, vibeEmbedding);

                    if (settings.DebugPipeline)
                    {
                        // Recompute individual Phase 2 signals for debug display
                        var qt = RelevanceScorer.Tokenize(queryText);
                        var (idf2, avgDocLen2) = RelevanceScorer.BuildCorpusStats(uniqueItems);

                        AnsiConsole.WriteLine();
                        var table = new Table()
                            .Title("[bold yellow]Phase 2: Full RRF (+ Query Similarity + Vibe Alignment)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]#[/]")
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]BM25[/]")
                            .AddColumn("[cyan]Fresh[/]")
                            .AddColumn("[cyan]Auth[/]")
                            .AddColumn("[cyan]QSim[/]")
                            .AddColumn("[cyan]Vibe[/]")
                            .AddColumn("[cyan]RRF[/]")
                            .AddColumn("[cyan]Title[/]");

                        var rank = 1;
                        foreach (var item in uniqueItems.Take(25))
                        {
                            var bm25 = RelevanceScorer.BM25Score(RelevanceScorer.ItemText(item), qt, idf2, avgDocLen2);
                            var fresh = RelevanceScorer.ComputeFreshness(item);
                            var auth = RelevanceScorer.NormalizeAuthority(item, uniqueItems);
                            var qSim = item.Embedding != null ? EmbeddingService.CosineSimilarity(item.Embedding, queryEmbedding) : 0f;
                            var vSim = vibeEmbedding != null && item.Embedding != null
                                ? EmbeddingService.CosineSimilarity(item.Embedding, vibeEmbedding) : 0f;

                            table.AddRow(
                                $"{rank++}",
                                Markup.Escape(item.Source),
                                $"{bm25:F2}",
                                $"{fresh:F2}",
                                $"{auth:F2}",
                                $"{qSim:F3}",
                                $"{vSim:F3}",
                                $"[bold]{item.RelevanceScore:F3}[/]",
                                Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title));
                        }
                        AnsiConsole.Write(table);
                    }

                    if (!settings.Quiet)
                    {
                        var topScore = uniqueItems.FirstOrDefault()?.RelevanceScore ?? 0;
                        var botScore = uniqueItems.LastOrDefault()?.RelevanceScore ?? 0;
                        AnsiConsole.MarkupLine($"[grey]RRF ranked {uniqueItems.Count} items (top={topScore:F3}, bot={botScore:F3})[/]");
                    }
                }

                // Stage 2.5a: Apply source reliability weights (RRF score multipliers)
                if (config.SourceFilter.Weights.Count > 0)
                {
                    var weightedCount = ApplySourceWeights(uniqueItems, config.SourceFilter);
                    if (!settings.Quiet && weightedCount > 0)
                        AnsiConsole.MarkupLine($"[grey]Source weights: {weightedCount} items adjusted[/]");

                    // Re-sort after weight adjustment
                    var weighted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                    uniqueItems.Clear();
                    uniqueItems.AddRange(weighted);
                }

                // Stage 2.5b: LFU diversity decay — penalize items returned too often
                if (uniqueItems.Count > 0)
                {
                    var itemIds = uniqueItems.Select(i => i.Id).ToList();
                    var usageStats = await storage.GetItemUsageAsync(itemIds);
                    if (usageStats.Count > 0)
                    {
                        var lfuAdjusted = 0;
                        foreach (var item in uniqueItems)
                        {
                            if (usageStats.TryGetValue(item.Id, out var usage) && usage.accessCount > 1)
                            {
                                // Mild decay: 1/(1 + 0.1 * log2(accessCount))
                                // 2 accesses → 0.91x, 4 → 0.83x, 8 → 0.77x, 16 → 0.71x
                                var decay = 1.0 / (1.0 + 0.1 * Math.Log2(usage.accessCount));
                                item.RelevanceScore *= decay;
                                lfuAdjusted++;
                            }
                        }

                        if (lfuAdjusted > 0)
                        {
                            // Re-sort after LFU decay
                            var lfuSorted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                            uniqueItems.Clear();
                            uniqueItems.AddRange(lfuSorted);

                            if (!settings.Quiet)
                                AnsiConsole.MarkupLine($"[grey]LFU diversity: {lfuAdjusted} frequently-seen items decayed[/]");
                        }
                    }
                }

                // Stage 2.5c: One-hop link following for richer context
                var linkCacheHits = 0;
                var linksSkippedByRelevance = 0;
                if (config.LinkFollowing.Enabled && !settings.NoLinks)
                {
                    var itemsToFollow = uniqueItems.Take(settings.Limit).ToList();
                    var linkTask = ctx.AddTask("[cyan]Following links[/]", maxValue: itemsToFollow.Count);

                    var linkService = new LinkFollowingService(
                        httpClient, config.LinkFollowing, storage,
                        embedder: embedding.Embed,
                        queryEmbedding: queryEmbedding);
                    var activityLog = new List<string>();

                    await linkService.FollowLinksAsync(
                        itemsToFollow,
                        new Progress<(int current, int total)>(p => linkTask.Value = p.current),
                        onActivity: activity =>
                        {
                            activityLog.Add(activity);
                            // Show last activity in progress description (strip markup for non-markup-safe display)
                            linkTask.Description = $"[cyan]{Markup.Remove(activity)}[/]";
                        });

                    var totalLinked = itemsToFollow.Sum(i => i.LinkedPages.Count);
                    var enrichedCount = itemsToFollow.Count(i => i.IsEnriched);
                    var structuredCount = itemsToFollow.Count(i => i.ContentStructure != null);
                    linkTask.Value = itemsToFollow.Count;
                    var cacheInfo = linkService.CacheHits > 0 ? $", {linkService.CacheHits} cached" : "";
                    var relevanceInfo = linkService.LinksSkippedByRelevance > 0 ? $", {linkService.LinksSkippedByRelevance} irrelevant skipped" : "";
                    linkTask.Description = $"[green]Enriched {enrichedCount} articles ({structuredCount} with structure), {totalLinked} linked pages{cacheInfo}{relevanceInfo}[/]";

                    if (settings.DebugPipeline)
                    {
                        AnsiConsole.MarkupLine($"[grey]Links: {enrichedCount} enriched, {totalLinked} linked, {linkService.CacheHits} cache hits, {linkService.LinksSkippedByRelevance} irrelevant skipped[/]");
                    }

                    // Re-embed items that were enriched with full article content
                    // (original embeddings were computed on short RSS descriptions)
                    if (enrichedCount > 0)
                    {
                        foreach (var item in itemsToFollow.Where(i => i.IsEnriched))
                        {
                            var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                            item.Embedding = embedding.Embed(textToEmbed);
                        }
                    }

                    // Capture stats for JSON output
                    linkCacheHits = linkService.CacheHits;
                    linksSkippedByRelevance = linkService.LinksSkippedByRelevance;
                }

                // In-corpus link authority ("silly PageRank"):
                // Articles that are linked by other articles in our corpus get an authority boost.
                var inLinkCounts = ComputeInCorpusLinkAuthority(uniqueItems);
                if (inLinkCounts.Count > 0)
                {
                    foreach (var item in uniqueItems)
                    {
                        var normalizedUrl = item.Url?.Split('?')[0].TrimEnd('/').ToLowerInvariant() ?? "";
                        if (inLinkCounts.TryGetValue(normalizedUrl, out var linkCount) && linkCount > 0)
                        {
                            // Boost: log scale so 2 in-links = +0.05, 5 = +0.08, 10 = +0.10
                            var boost = Math.Min(0.10, Math.Log2(1 + linkCount) * 0.035);
                            item.RelevanceScore = Math.Min(1.0, item.RelevanceScore + boost);
                        }
                    }

                    // Re-sort after boost
                    var boostedItems = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                    uniqueItems.Clear();
                    uniqueItems.AddRange(boostedItems);

                    if (!settings.Quiet && inLinkCounts.Values.Any(c => c > 0))
                    {
                        var boostedCount = inLinkCounts.Count(kv => kv.Value > 0);
                        AnsiConsole.MarkupLine($"[grey]In-corpus PageRank: {boostedCount} items boosted by cross-references[/]");
                    }
                }

                // Stage 3: Deterministic signal analysis — no LLM
                // Segments, sentiment, topic all computed via ONNX embeddings and article processing.
                // The LLM is reserved for Stage 4 (synthesis) only.
                var analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();

                {
                    var itemsToAnalyze = uniqueItems.Take(settings.Limit).ToList();

                    // Pre-compute anchor embeddings once for sentiment and topic inference
                    var positiveAnchor = embedding.Embed(RelevanceScorer.PositiveAnchorText);
                    var negativeAnchor = embedding.Embed(RelevanceScorer.NegativeAnchorText);
                    var topicAnchors = RelevanceScorer.TopicAnchorTexts.ToDictionary(
                        kv => kv.Key,
                        kv => embedding.Embed(kv.Value));

                    // Split: items with existing summaries skip re-analysis
                    var alreadyAnalyzed = itemsToAnalyze
                        .Where(i => !string.IsNullOrEmpty(i.Summary) && i.Summary != i.Title)
                        .ToList();
                    var needsAnalysis = itemsToAnalyze
                        .Where(i => string.IsNullOrEmpty(i.Summary) || i.Summary == i.Title)
                        .ToList();

                    if (alreadyAnalyzed.Count > 0 && !settings.Quiet)
                        AnsiConsole.MarkupLine($"[grey]Skipping analysis for {alreadyAnalyzed.Count} pre-analyzed items[/]");

                    foreach (var item in alreadyAnalyzed)
                    {
                        analyzedItems.Add((item.Title, item.Summary!, item.DetectedTopic ?? "general",
                            item.SentimentScore, item.Url ?? "", item.RelevanceScore));
                    }

                    var analyzeTask = ctx.AddTask("[cyan]Analyzing content[/]", maxValue: Math.Max(1, needsAnalysis.Count));
                    if (needsAnalysis.Count == 0)
                    {
                        analyzeTask.Value = 1;
                        analyzeTask.Description = $"[green]Analyzed {analyzedItems.Count} items ({alreadyAnalyzed.Count} cached)[/]";
                    }
                    else
                    {
                        // Phase 1: Segment extraction via ArticleProcessor (CPU-bound)
                        using var articleProcessor = new ArticleProcessor();

                        foreach (var item in needsAnalysis)
                        {
                            try
                            {
                                var processed = await articleProcessor.ProcessAsync(item);

                                // Summary from top salience segments (deterministic, no LLM)
                                var topSegments = processed.TopSegments
                                    .OrderByDescending(s => s.SalienceScore)
                                    .Take(3)
                                    .ToList();
                                item.Summary = topSegments.Count > 0
                                    ? string.Join(" ", topSegments.Select(s =>
                                        s.Text.Length > 200 ? s.Text[..200] : s.Text))
                                    : (item.Content?.Length > 300
                                        ? item.Content[..300] + "..."
                                        : item.Content ?? item.Title);

                                // Structural analysis
                                if (item.Content != null)
                                    item.ContentStructure = MarkdownContentAnalyzer.Analyze(item.Content);
                            }
                            catch
                            {
                                // Segmentation failed — use content truncation
                                var content = item.Content ?? "";
                                item.Summary = content.Length > 300
                                    ? content[..300] + "..."
                                    : (content.Length > 0 ? content : item.Title);
                            }

                            // Phase 2: Embedding-based sentiment + topic (pure math)
                            if (item.Embedding != null)
                            {
                                item.SentimentScore = RelevanceScorer.ComputeEmbeddingSentiment(
                                    item.Embedding, positiveAnchor, negativeAnchor);
                                item.DetectedTopic = RelevanceScorer.InferTopic(item.Embedding, topicAnchors);
                            }
                            else
                            {
                                item.DetectedTopic = InferTopicFromSource(item.Source);
                            }

                            analyzedItems.Add((item.Title, item.Summary!, item.DetectedTopic ?? "general",
                                item.SentimentScore, item.Url ?? "", item.RelevanceScore));
                            analyzeTask.Increment(1);
                        }
                    }

                    // Save to storage
                    foreach (var item in itemsToAnalyze)
                        await storage.SaveItemAsync(item);

                    analyzeTask.Description = $"[green]Analyzed {analyzedItems.Count} items (deterministic)[/]";
                }

                // Debug: Show enriched signals
                if (settings.DebugPipeline && analyzedItems.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var table = new Table()
                        .Title("[bold yellow]Signal Enrichment: Sentiment + Topic + Relevance[/]")
                        .Border(TableBorder.Rounded)
                        .AddColumn("[cyan]#[/]")
                        .AddColumn("[cyan]Source[/]")
                        .AddColumn("[cyan]Topic[/]")
                        .AddColumn("[cyan]Sent[/]")
                        .AddColumn("[cyan]RRF[/]")
                        .AddColumn("[cyan]Title[/]")
                        .AddColumn("[cyan]Snippet[/]");

                    var rank = 1;
                    foreach (var item in analyzedItems.OrderByDescending(i => i.relevance).Take(20))
                    {
                        var sentColor = item.sentiment > 0.1f ? "green" : item.sentiment < -0.1f ? "red" : "grey";
                        var snippet = item.summary.Length > 60
                            ? item.summary[..57] + "..."
                            : item.summary;
                        // Remove newlines from snippet
                        snippet = snippet.Replace("\n", " ").Replace("\r", "");

                        table.AddRow(
                            $"{rank++}",
                            Markup.Escape(GetSourceFromUrl(item.url)),
                            $"[bold]{Markup.Escape(item.topic)}[/]",
                            $"[{sentColor}]{item.sentiment:F2}[/]",
                            $"{item.relevance:F3}",
                            Markup.Escape(item.title.Length > 40 ? item.title[..37] + "..." : item.title),
                            Markup.Escape(snippet));
                    }
                    AnsiConsole.Write(table);

                    // Show structural analysis for enriched items
                    var enrichedWithStructure = uniqueItems
                        .Where(i => i.IsEnriched && i.ContentStructure != null)
                        .OrderByDescending(i => i.RelevanceScore)
                        .Take(10)
                        .ToList();

                    if (enrichedWithStructure.Count > 0)
                    {
                        AnsiConsole.WriteLine();
                        var structTable = new Table()
                            .Title("[bold yellow]Structural Analysis (Enriched Articles)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]Type[/]")
                            .AddColumn("[cyan]Quality[/]")
                            .AddColumn("[cyan]Structure[/]")
                            .AddColumn("[cyan]Title[/]");

                        foreach (var item in enrichedWithStructure)
                        {
                            var s = item.ContentStructure!;
                            var qColor = s.QualityScore > 0.5 ? "green" : s.QualityScore > 0.25 ? "yellow" : "red";
                            structTable.AddRow(
                                Markup.Escape(GetSourceFromUrl(item.Url ?? "")),
                                $"[bold]{Markup.Escape(s.ContentType)}[/]",
                                $"[{qColor}]{s.QualityScore:F2}[/]",
                                Markup.Escape(s.ToSummary()),
                                Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title));
                        }
                        AnsiConsole.Write(structTable);
                    }
                }

                // NER entity extraction (--entities or --graph)
                var allEntities = new List<NerEntity>();
                var articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
                var extractEntities = settings.Entities; // NER is ONNX-based, no LLM needed

                if (extractEntities)
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

                            // Track per-article entities for knowledge graph
                            var matchingContentItem = uniqueItems.FirstOrDefault(u =>
                                string.Equals(u.Title, item.title, StringComparison.Ordinal));
                            if (matchingContentItem != null && entities.Count > 0)
                            {
                                articleEntityMap.Add((matchingContentItem, entities));
                            }

                            nerTask.Increment(1);
                        }
                        // Dedupe entities for display
                        allEntities = allEntities
                            .GroupBy(e => e.Text.ToLowerInvariant())
                            .Select(g => g.OrderByDescending(e => e.Confidence).First())
                            .OrderByDescending(e => e.Confidence)
                            .ToList();
                    }
                    nerTask.Description = $"[green]Found {allEntities.Count} entities[/]";
                }

                // Index item embeddings into DuckDB for HNSW similarity search (skip in --no-llm fast mode)
                if (settings.Graph && vectorStore != null)
                {
                    var indexTask = ctx.AddTask("[cyan]Indexing embeddings[/]", maxValue: 100);
                    var graphService = new KnowledgeGraphService(vectorStore);
                    var itemsWithEmbeddings = uniqueItems
                        .Where(i => i.Embedding != null)
                        .Take(settings.Limit)
                        .ToList();
                    await graphService.IndexItemEmbeddingsAsync(itemsWithEmbeddings);
                    indexTask.Value = 100;
                    indexTask.Description = $"[green]Indexed {itemsWithEmbeddings.Count} embeddings[/]";
                }

                // Ingest entities into knowledge graph
                if (settings.Graph && vectorStore != null && articleEntityMap.Count > 0)
                {
                    var graphTask = ctx.AddTask("[cyan]Building knowledge graph[/]", maxValue: 100);
                    var graphService = new KnowledgeGraphService(vectorStore);
                    await graphService.IngestEntitiesAsync(articleEntityMap);

                    // Ingest linked page entities with lower confidence
                    foreach (var (item, _) in articleEntityMap)
                    {
                        if (item.LinkedPages.Count > 0)
                        {
                            using var linkedNer = new NerService();
                            if (linkedNer.IsAvailable)
                            {
                                await linkedNer.InitializeAsync();
                                foreach (var linked in item.LinkedPages)
                                {
                                    var linkedEntities = await linkedNer.ExtractEntitiesAsync(
                                        $"{linked.Title} {linked.Content}");
                                    if (linkedEntities.Count > 0)
                                    {
                                        await graphService.IngestLinkedPageEntitiesAsync(
                                            item, linkedEntities, linked.Url);
                                    }
                                }
                            }
                        }
                    }

                    graphTask.Value = 100;
                    var (ec, rc, mc, ic) = await vectorStore.GetStatsAsync();
                    graphTask.Description = $"[green]Graph: {ec} entities, {rc} relationships, {ic} items[/]";
                }

                // Stage 4: Generate summary
                var summaryTask = ctx.AddTask("[cyan]Generating summary[/]", maxValue: 100);
                var template = settings.Template.ToLowerInvariant();
                var isBlogTemplate = template is "blog-article" or "blog-timeline"
                    or "blog-newsletter" or "blog-newsletter-html";

                string finalSummary;
                DigestData? templateData = null;

                if (ollamaAvailable && analyzedItems.Count > 0)
                {
                    summaryTask.Value = 10;
                    var userQuery = interpreted?.RawPrompt ?? settings.Prompt;

                    // Detect query type for source quality weighting and template auto-selection.
                    // Use sentinel intent when available — the LLM is better at distinguishing
                    // QA from roundup (e.g., "What's the SNL host this week?" is QA, not roundup).
                    var detectedQueryType = QueryTypeDetector.Detect(userQuery, interpreted?.SentinelIntent);

                    // Apply source quality multipliers based on query type
                    if (detectedQueryType is QueryType.Timeline or QueryType.Explainer
                            or QueryType.Roundup)
                    {
                        var qualityAdjusted = 0;
                        foreach (var item in uniqueItems)
                        {
                            var multiplier = QueryTypeDetector.GetSourceQualityMultiplier(
                                detectedQueryType, item.Url);
                            if (Math.Abs(multiplier - 1.0) > 0.01)
                            {
                                item.RelevanceScore *= multiplier;
                                qualityAdjusted++;
                            }
                        }
                        if (qualityAdjusted > 0)
                        {
                            var sorted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                            uniqueItems.Clear();
                            uniqueItems.AddRange(sorted);
                            // Also re-sort analyzedItems to match
                            analyzedItems = analyzedItems
                                .OrderByDescending(a => a.relevance)
                                .ToList();

                            if (!settings.Quiet)
                                AnsiConsole.MarkupLine(
                                    $"[grey]Source quality ({detectedQueryType}): {qualityAdjusted} items adjusted[/]");
                        }
                    }

                    summaryTask.Value = 20;

                    // Resolve YAML template definition (if any)
                    var templateDef = outputTemplates.GetDefinition(template);
                    var effectiveBase = templateDef?.BaseTemplate ?? template;
                    var isBlogArticle = effectiveBase is "blog-article" or "blog-timeline"
                                        || template is "blog-article" or "blog-timeline";

                    // Route to appropriate synthesis based on template
                    if (isBlogArticle)
                    {
                        // Force timeline for blog-timeline, otherwise auto-detect
                        var articleQueryType = effectiveBase == "blog-timeline" || template == "blog-timeline"
                            ? QueryType.Timeline
                            : detectedQueryType;

                        var blogResult = await ollama.SynthesizeBlogArticleAsync(
                            analyzedItems, vibe, vibePrompt,
                            userQuery ?? "topic overview",
                            articleQueryType,
                            uniqueItems, embedding.Embed, templateDef, cancellationToken);

                        // Build template data
                        templateData = new DigestData
                        {
                            Date = DateTimeOffset.Now,
                            Vibe = vibe,
                            Query = userQuery,
                            ArticleTitle = blogResult.Title,
                            Introduction = blogResult.Introduction,
                            Sections = blogResult.Sections
                                .Select(s => new DigestSection(s.Heading, s.Content, s.SourceUrls))
                                .ToList(),
                            Conclusion = blogResult.Conclusion,
                            SourceUrls = blogResult.SourceUrls,
                            Items = analyzedItems.Select(a => new DigestItem
                            {
                                Title = a.title,
                                Url = a.url,
                                Summary = a.summary,
                                Topic = a.topic,
                                Sentiment = a.sentiment
                            }).ToList()
                        };

                        // Use YAML template's own Liquid template if registered, else base template
                        var renderTemplate = templateDef?.Template != null ? template
                            : effectiveBase == "blog-timeline" ? "blog-timeline"
                            : "blog-article";
                        finalSummary = outputTemplates.Render(templateData, renderTemplate);
                    }
                    else if (template is "blog-newsletter" or "blog-newsletter-html")
                    {
                        var newsletterResult = await ollama.SynthesizeNewsletterAsync(
                            analyzedItems, vibe, vibePrompt,
                            userQuery,
                            uniqueItems, embedding.Embed, cancellationToken);

                        templateData = new DigestData
                        {
                            Date = DateTimeOffset.Now,
                            Vibe = vibe,
                            Query = userQuery,
                            Introduction = newsletterResult.Introduction,
                            TopPicks = newsletterResult.TopPicks
                                .Select(p => new DigestPick(p.Title, p.Url, p.Commentary, p.Source))
                                .ToList(),
                            QuickHits = newsletterResult.QuickHits
                                .Select(q => new DigestQuickHit(q.Title, q.Url, q.OneLiner))
                                .ToList(),
                            SignOff = newsletterResult.SignOff,
                            Items = analyzedItems.Select(a => new DigestItem
                            {
                                Title = a.title,
                                Url = a.url,
                                Summary = a.summary,
                                Topic = a.topic,
                                Sentiment = a.sentiment
                            }).ToList()
                        };

                        finalSummary = outputTemplates.Render(templateData,
                            template == "blog-newsletter-html" ? "blog-newsletter-html" : "blog-newsletter");
                    }
                    else
                    {
                        // Standard synthesis path
                        summaryTask.Value = 50;

                        // Entity disambiguation: detect ambiguous entities in top items
                        // Skip for roundup queries — they are about collecting diverse stories
                        if (!string.IsNullOrWhiteSpace(userQuery) && detectedQueryType != QueryType.Roundup)
                        {
                            var topForDisambig = uniqueItems
                                .OrderByDescending(i => i.RelevanceScore)
                                .Take(settings.Limit)
                                .ToList();

                            var disambiguator = new EntityDisambiguationService();
                            var disambiguation = await disambiguator.DisambiguateFastAsync(
                                topForDisambig, userQuery, embedding, storage);

                            if (disambiguation.IsAmbiguous && disambiguation.Clusters.Count >= 2)
                            {
                                var entityLines = disambiguation.Clusters
                                    .Select(c => $"- Entity: \"{c.Label}\"")
                                    .ToList();

                                userQuery = $"""
                                    IMPORTANT: Evidence contains distinct entities with similar names.
                                    Summarize EACH entity separately under its own heading:
                                    {string.Join("\n", entityLines)}
                                    Do NOT conflate these into one entity.

                                    ORIGINAL QUERY: {userQuery}
                                    """;
                            }
                        }

                        finalSummary = await ollama.SynthesizeSummaryAsync(
                            analyzedItems, vibe, vibePrompt, userQuery, uniqueItems,
                            embedder: embedding.Embed);
                    }
                }
                else
                {
                    finalSummary = GenerateFallbackSummary(analyzedItems, vibe);
                }

                summaryTask.Value = 100;
                summaryTask.Description = isBlogTemplate
                    ? $"[green]{template} generated[/]"
                    : "[green]Summary generated[/]";

                // Save summary
                await storage.SaveSummaryAsync(vibe, finalSummary, analyzedItems.Count);

                // Log query for segment reuse (LFU tracking + similar query matching)
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    var returnedIds = uniqueItems.Take(settings.Limit).Select(i => i.Id).ToList();
                    var logEmbedding = earlyQueryEmbedding ?? (queryText.Length > 0 ? embedding.Embed(queryText) : null);
                    await storage.LogQueryAsync(queryText, logEmbedding, vibe, returnedIds);
                }

                // Output
                if (settings.Json)
                {
                    // Token-efficient structured JSON for LLM tool / agent consumption.
                    // Optimized to be useful as a tool response: compact, fact-dense,
                    // source-attributed, no markdown noise.
                    // Tiers: "full" (LLM synthesis), "signals" (--nollm, ONNX-only).

                    var jsonQuery = interpreted?.RawPrompt ?? settings.Prompt;

                    // Per-item TextRank excerpts (most informative sentences, no LLM needed)
                    var itemExcerpts = new Dictionary<string, string>();
                    var keyFactCandidates = new List<(string fact, double relevance, string title, string url)>();

                    foreach (var item in uniqueItems.Take(settings.Limit))
                    {
                        var content = item.Content ?? "";
                        if (content.Length > 200)
                        {
                            try
                            {
                                var excerpt = StripMarkdownForLlm(
                                    TextRankExtractor.ExtractKeySentences(
                                        content, embedding.Embed, maxChars: 400));
                                itemExcerpts[item.Id] = excerpt;

                                // Top-relevance articles contribute their lead fact
                                if (item.RelevanceScore > 0.3)
                                {
                                    var dotIdx = excerpt.IndexOf(". ", StringComparison.Ordinal);
                                    var leadFact = dotIdx > 20 ? excerpt[..(dotIdx + 1)] : excerpt;
                                    if (leadFact.Length > 250) leadFact = leadFact[..250] + "...";
                                    keyFactCandidates.Add((leadFact, item.RelevanceScore,
                                        item.Title, item.Url ?? ""));
                                }
                            }
                            catch
                            {
                                itemExcerpts[item.Id] = StripMarkdownForLlm(
                                    content.Length > 400 ? content[..400] + "..." : content);
                            }
                        }
                        else if (content.Length > 0)
                        {
                            itemExcerpts[item.Id] = StripMarkdownForLlm(content);
                        }
                    }

                    // Cross-article key facts: source-attributed, one per top article
                    var keyFacts = keyFactCandidates
                        .OrderByDescending(k => k.relevance)
                        .Take(7)
                        .Select(k => new
                        {
                            fact = k.fact,
                            source = GetSourceFromUrl(k.url),
                            url = k.url
                        })
                        .ToArray();

                    // Source diversity
                    var itemsForStats = uniqueItems.Take(settings.Limit).ToList();
                    var sourceDistribution = itemsForStats
                        .GroupBy(i => i.Source)
                        .ToDictionary(g => g.Key, g => g.Count());

                    var sentimentBreakdown = new
                    {
                        positive = analyzedItems.Count(i => i.sentiment > 0.15f),
                        neutral = analyzedItems.Count(i => i.sentiment is >= -0.15f and <= 0.15f),
                        negative = analyzedItems.Count(i => i.sentiment < -0.15f)
                    };

                    var themeData = ExtractKeyThemes(analyzedItems, uniqueItems);

                    // In signals mode, replace verbose fallback summary with compact version
                    var jsonSummary = ollamaAvailable
                        ? finalSummary
                        : (keyFacts.Length > 0
                            ? string.Join(" ", keyFacts.Select(f => f.fact))
                            : $"Found {analyzedItems.Count} items for \"{jsonQuery}\".");

                    var jsonOutput = new
                    {
                        meta = new
                        {
                            query = jsonQuery,
                            vibe,
                            generated = DateTimeOffset.UtcNow,
                            pipeline = ollamaAvailable ? "full" : "signals",
                            itemCount = analyzedItems.Count,
                            sources = new
                            {
                                unique = sourceDistribution.Count,
                                distribution = sourceDistribution
                            },
                            sentiment = sentimentBreakdown,
                            cache = linkCacheHits > 0 || linksSkippedByRelevance > 0
                                ? new { hits = linkCacheHits, irrelevantSkipped = linksSkippedByRelevance }
                                : null
                        },
                        summary = jsonSummary,
                        keyFacts,
                        themes = new
                        {
                            topics = themeData.topics.Select(tp => new { tp.topic, tp.count }).ToArray(),
                            keyTerms = themeData.terms.Select(tr => new { tr.term, tr.articles }).ToArray()
                        },
                        items = analyzedItems.Select(i =>
                        {
                            var contentItem = uniqueItems.FirstOrDefault(u =>
                                string.Equals(u.Title, i.title, StringComparison.Ordinal));
                            var hasExcerpt = contentItem != null
                                && itemExcerpts.TryGetValue(contentItem.Id, out var ex);
                            return new
                            {
                                i.title,
                                i.url,
                                source = contentItem?.Source ?? GetSourceFromUrl(i.url),
                                i.topic,
                                i.sentiment,
                                i.relevance,
                                quality = contentItem?.ContentStructure?.QualityScore,
                                excerpt = hasExcerpt
                                    ? itemExcerpts[contentItem!.Id]
                                    : StripMarkdownForLlm(
                                        i.summary.Length > 400 ? i.summary[..400] + "..." : i.summary),
                                linkedCount = contentItem?.LinkedPages.Count ?? 0
                            };
                        }).ToArray(),
                        entities = extractEntities ? allEntities.Select(e => new
                        {
                            text = e.Text,
                            type = e.Type,
                            confidence = e.Confidence
                        }).ToArray() : null
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(jsonOutput,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                        });

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
                    var renderedMarkup = MarkdownToSpectre(finalSummary);
                    var header = isBlogTemplate
                        ? $"[bold cyan]{Markup.Escape(template)}[/]"
                        : $"[bold cyan]Doom Scroll Digest ({vibe})[/]";
                    AnsiConsole.Write(new Panel(renderedMarkup)
                        .Header(header)
                        .Border(BoxBorder.Rounded)
                        .Padding(1, 1));

                    // Display key themes from analyzed content
                    if (analyzedItems.Count > 0)
                    {
                        var themes = ExtractKeyThemes(analyzedItems, uniqueItems);
                        if (themes.topics.Count > 0 || themes.terms.Count > 0)
                        {
                            AnsiConsole.WriteLine();
                            var themesParts = new List<string>();

                            // Topic distribution as colored tags
                            if (themes.topics.Count > 0)
                            {
                                var topicTags = themes.topics.Select(t =>
                                {
                                    var color = t.topic.ToLowerInvariant() switch
                                    {
                                        "technology" => "blue",
                                        "ai" or "machine_learning" => "magenta",
                                        "security" => "red",
                                        "science" => "cyan",
                                        "health" => "green",
                                        "business" or "economy" => "yellow",
                                        "politics" => "red",
                                        "world" => "aqua",
                                        "entertainment" or "humor" => "fuchsia",
                                        "climate" or "environment" => "green",
                                        "space" => "blue",
                                        _ => "grey"
                                    };
                                    var label = char.ToUpper(t.topic[0]) + t.topic[1..];
                                    return $"[{color}]{Markup.Escape(label)}[/] [dim]({t.count})[/]";
                                });
                                themesParts.Add(string.Join("  ", topicTags));
                            }

                            // Key cross-article terms
                            if (themes.terms.Count > 0)
                            {
                                var termTags = themes.terms.Select(t =>
                                    $"[bold]{Markup.Escape(t.term)}[/][dim]×{t.articles}[/]");
                                themesParts.Add(string.Join("  ", termTags));
                            }

                            AnsiConsole.Write(new Panel(string.Join("\n", themesParts))
                                .Header("[bold yellow]Key Themes[/]")
                                .Border(BoxBorder.Rounded)
                                .Padding(1, 0));
                        }
                    }

                    // Display entities if requested
                    if (extractEntities && allEntities.Count > 0)
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

                        // Story Connections: show articles linked by shared entities
                        if (articleEntityMap.Count >= 2)
                        {
                            DisplayStoryConnections(articleEntityMap);
                        }
                    }

                    // Display knowledge graph (skip in --no-llm fast mode)
                    if (settings.Graph && vectorStore != null)
                    {
                        var graphService = new KnowledgeGraphService(vectorStore);
                        await graphService.DisplayGraphAsync(topN: 15, daysBack: 7);
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
                if (vectorStore != null)
                    await vectorStore.CleanupAsync(config.Storage.RetentionDays);
            });

        if (vectorStore != null)
            await vectorStore.DisposeAsync();

        return 0;
    }

    /// <summary>
    /// Prepend vibe-appropriate qualifiers to a search query
    /// so search results better match the desired sentiment.
    /// </summary>
    internal static readonly HashSet<string> PredefinedVibes =
        new(["doom", "hopeful", "snarky", "neutral"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the vibe is custom arbitrary text rather than a predefined vibe.
    /// </summary>
    internal static bool IsCustomVibe(string vibe) =>
        !PredefinedVibes.Contains(vibe);

    internal static string QualifySearchQuery(string query, string vibe)
    {
        var qualifier = vibe.ToLowerInvariant() switch
        {
            "doom" => "concerning problems risks issues in",
            "hopeful" => "positive breakthrough innovative upbeat news about",
            "snarky" => "controversial debate criticism of",
            "neutral" => "",
            _ => $"{vibe} articles stories about" // Custom vibe used directly as qualifier
        };

        return string.IsNullOrEmpty(qualifier) ? query : $"{qualifier} {query}";
    }

    /// <summary>
    /// Get representative text for a vibe to use as an embedding target
    /// for cosine-similarity-based sentiment scoring.
    /// </summary>
    internal static string GetVibeRepresentativeText(string vibe)
    {
        return vibe.ToLowerInvariant() switch
        {
            "doom" => "security vulnerability breach layoffs downturn recession failure risk crisis warning concerning problem threat",
            "hopeful" => "innovation breakthrough positive growth opportunity success launch improvement exciting new achievement progress",
            "snarky" => "hype overrated controversy debate criticism reality check failure ironic absurd",
            "neutral" => "technology software engineering development news",
            _ => vibe // Custom vibe text used directly as embedding target
        };
    }

    /// <summary>
    /// Infer a basic topic from the source name when embeddings aren't available.
    /// </summary>
    internal static string InferTopicFromSource(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "hn" => "technology",
            "reddit" => "technology",
            "bbc" or "guardian" or "cnn" or "reuters" => "world",
            "gnews" => "general",
            "so" => "technology",
            "ars" or "verge" => "technology",
            _ => "general"
        };
    }

    /// <summary>
    /// Convert markdown to Spectre.Console markup for rich console rendering.
    /// Handles headings, bold, italic, links, bullets, and horizontal rules.
    /// </summary>
    internal static string MarkdownToSpectre(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return "";

        var lines = markdown.Split('\n');
        var result = new System.Text.StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Horizontal rule
            if (line.Trim() is "---" or "***" or "___")
            {
                result.AppendLine("[dim]────────────────────────────────────────[/]");
                continue;
            }

            // Headings
            if (line.StartsWith("### "))
            {
                var text = Markup.Escape(line[4..].Trim());
                result.AppendLine($"[bold cyan]{text}[/]");
                continue;
            }
            if (line.StartsWith("## "))
            {
                var text = Markup.Escape(line[3..].Trim());
                result.AppendLine($"[bold underline yellow]{text}[/]");
                continue;
            }
            if (line.StartsWith("# "))
            {
                var text = Markup.Escape(line[2..].Trim());
                result.AppendLine($"[bold underline green]{text}[/]");
                continue;
            }

            // Bullet points
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                var indent = line.Length - line.TrimStart().Length;
                var bulletText = line.TrimStart()[2..];
                var indentStr = new string(' ', indent);
                result.AppendLine($"{indentStr}[cyan]•[/] {FormatInlineMarkdown(bulletText)}");
                continue;
            }

            // Regular line — process inline markdown
            result.AppendLine(FormatInlineMarkdown(line));
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// Process inline markdown: **bold**, *italic*, [links](url), `code`.
    /// Uses placeholder tokens so markdown patterns are processed on raw text
    /// (with their content escaped), then remaining literal text is escaped afterward.
    /// </summary>
    private static string FormatInlineMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var replacements = new List<string>();
        string Store(string spectreMarkup)
        {
            var idx = replacements.Count;
            replacements.Add(spectreMarkup);
            return $"\x01{idx}\x02";
        }

        // Process patterns on raw text (order matters: code > links > bold > italic)
        // Each captured group's content is Markup.Escaped individually.

        // Inline code: `code` → [grey on grey15]code[/]
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"`([^`]+)`",
            m => Store($"[grey on grey15]{Markup.Escape(m.Groups[1].Value)}[/]"));

        // Links: [text](url) → [cyan underline]text[/] [dim](url)[/]
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\[([^\]]+)\]\(([^)]+)\)",
            m => Store($"[cyan underline]{Markup.Escape(m.Groups[1].Value)}[/] [dim]({Markup.Escape(m.Groups[2].Value)})[/]"));

        // Bold: **text** → [bold]text[/]
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\*\*(.+?)\*\*",
            m => Store($"[bold]{Markup.Escape(m.Groups[1].Value)}[/]"));

        // Italic: *text* → [italic]text[/] (single asterisks not consumed by bold)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)",
            m => Store($"[italic]{Markup.Escape(m.Groups[1].Value)}[/]"));

        // Escape remaining literal text (Markup.Escape only affects [ and ],
        // so \x01/\x02 placeholder chars pass through untouched)
        text = Markup.Escape(text);

        // Restore placeholders with their Spectre markup
        for (var i = 0; i < replacements.Count; i++)
            text = text.Replace($"\x01{i}\x02", replacements[i]);

        return text;
    }

    private static string GetSourceFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try
        {
            var host = new Uri(url).Host.Replace("www.", "");
            return host.Split('.')[0];
        }
        catch { return "?"; }
    }

    /// <summary>
    /// Filter items by allowed/blocked domain lists.
    /// AllowedDomains = allowlist (if non-empty, only these pass).
    /// BlockedDomains = blocklist (removed after allow check).
    /// </summary>
    internal static List<ContentItem> ApplySourceDomainFilter(
        List<ContentItem> items, SourceFilterConfig filter)
    {
        var result = new List<ContentItem>(items.Count);

        foreach (var item in items)
        {
            var host = GetHostFromUrl(item.Url);
            if (string.IsNullOrEmpty(host))
            {
                result.Add(item); // Keep items without URLs (e.g., local content)
                continue;
            }

            // Allowlist: if configured, item must match
            if (filter.AllowedDomains.Count > 0)
            {
                if (!filter.AllowedDomains.Any(d =>
                    host.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            // Blocklist: remove matching items
            if (filter.BlockedDomains.Any(d =>
                host.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// Apply source reliability weights as RRF score multipliers.
    /// Matches by source name first, then by URL domain.
    /// Returns count of items that had weights applied.
    /// </summary>
    internal static int ApplySourceWeights(
        List<ContentItem> items, SourceFilterConfig filter)
    {
        var count = 0;

        foreach (var item in items)
        {
            var weight = 1.0;

            // Match by source name first (hn, reddit, bbc, gnews, search, etc.)
            if (filter.Weights.TryGetValue(item.Source, out var sourceWeight))
            {
                weight = sourceWeight;
            }
            else
            {
                // Fall back to domain matching
                var host = GetHostFromUrl(item.Url);
                if (!string.IsNullOrEmpty(host))
                {
                    foreach (var (pattern, w) in filter.Weights)
                    {
                        if (host.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            weight = w;
                            break;
                        }
                    }
                }
            }

            if (Math.Abs(weight - 1.0) > 0.001)
            {
                item.RelevanceScore = Math.Min(1.0, item.RelevanceScore * weight);
                count++;
            }
        }

        return count;
    }

    private static string? GetHostFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return null; }
    }

    /// <summary>
    /// Strip markdown formatting for clean LLM consumption.
    /// Removes headers, link syntax, emphasis, code fences — keeps plain text facts.
    /// </summary>
    internal static string StripMarkdownForLlm(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Remove markdown headers (## Header)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        // Remove markdown images ![alt](url) — complete or truncated
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)?", "$1");
        // Remove truncated image at end: ![text](url-without-close...
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*$", "$1");
        // Remove bare ![ at start if no matching ]
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^!\[", "");
        // Convert [text](url) links to just "text" — complete or truncated
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)?", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]*$", "$1");
        // Remove emphasis markers (*text*, **text**, _text_, __text__)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\*{1,2}|_{1,2})(.+?)\1", "$2");
        // Remove escaped special chars (\( \) \[ \] etc.)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\\([()[\]])", "$1");
        // Remove code fences and inline code
        text = text.Replace("```", "").Replace("~~~", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");
        // Remove list markers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*[-*+]\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*\d+\.\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        // Collapse whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        return text;
    }

    /// <summary>
    /// Display story connections based on shared named entities.
    /// Shows which articles are linked by people, organizations, locations.
    /// </summary>
    private static void DisplayStoryConnections(
        List<(ContentItem item, List<NerEntity> entities)> articleEntityMap)
    {
        // Build entity → articles index (normalize entity text)
        var entityToArticles = new Dictionary<string, List<(ContentItem item, NerEntity entity)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (item, entities) in articleEntityMap)
        {
            foreach (var entity in entities.Where(e => e.Confidence >= 0.6))
            {
                var key = entity.Text.Trim();
                if (key.Length < 2) continue;
                if (!entityToArticles.ContainsKey(key))
                    entityToArticles[key] = [];
                // Avoid duplicate articles per entity
                if (entityToArticles[key].All(a => a.item.Id != item.Id))
                    entityToArticles[key].Add((item, entity));
            }
        }

        // Find entities that connect 2+ articles (these are the interesting ones)
        var connections = entityToArticles
            .Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Max(a => a.entity.Confidence))
            .Take(10)
            .ToList();

        if (connections.Count == 0) return;

        AnsiConsole.WriteLine();
        var tree = new Tree("[bold yellow]Story Connections[/]")
            .Style(Style.Parse("dim"));

        foreach (var (entityText, articles) in connections)
        {
            var typeColor = articles[0].entity.Type switch
            {
                "PER" => "green",
                "ORG" => "blue",
                "LOC" => "yellow",
                _ => "grey"
            };
            var typeLabel = articles[0].entity.Type switch
            {
                "PER" => "person",
                "ORG" => "org",
                "LOC" => "place",
                "MISC" => "topic",
                _ => "entity"
            };

            var node = tree.AddNode(
                $"[{typeColor} bold]{Markup.Escape(entityText)}[/] [dim]({typeLabel}, {articles.Count} stories)[/]");

            foreach (var (item, _) in articles.Take(5))
            {
                var title = item.Title.Length > 60
                    ? item.Title[..57] + "..."
                    : item.Title;
                var source = GetSourceFromUrl(item.Url ?? "");
                node.AddNode($"[dim]{source}[/] {Markup.Escape(title)}");
            }
        }

        AnsiConsole.Write(tree);
    }

    private static string GenerateFallbackSummary(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> items,
        string vibe)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Doom Scroll Digest ({vibe})");
        sb.AppendLine();
        sb.AppendLine($"*Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} | {items.Count} items ranked by RRF (BM25 + embeddings + freshness)*");
        sb.AppendLine();

        // === TOP STORIES: Show the highest-confidence items first ===
        var topItems = items
            .OrderByDescending(i => i.relevance)
            .Take(Math.Min(5, items.Count))
            .ToList();

        if (topItems.Count > 0)
        {
            sb.AppendLine("## Top Stories");
            sb.AppendLine();

            var maxRelevance = topItems.Max(i => i.relevance);

            foreach (var item in topItems)
            {
                // Confidence bar: normalize to 0-100% relative to max score
                var confidence = maxRelevance > 0 ? item.relevance / maxRelevance : 0;
                var pct = (int)(confidence * 100);
                var bar = new string('#', pct / 10) + new string('.', 10 - pct / 10);
                var sentimentIcon = item.sentiment switch
                {
                    > 0.15f => "+",
                    < -0.15f => "-",
                    _ => "~"
                };

                if (!string.IsNullOrEmpty(item.url))
                    sb.AppendLine($"  [{sentimentIcon}] [{bar}] {pct}% | {item.title}");
                else
                    sb.AppendLine($"  [{sentimentIcon}] [{bar}] {pct}% | {item.title}");

                // Show content snippet for top stories — this is the "segment" the user wants
                if (!string.IsNullOrEmpty(item.summary) && item.summary != item.title)
                {
                    var truncated = item.summary.Length > 300
                        ? item.summary[..300] + "..."
                        : item.summary;
                    sb.AppendLine($"      {truncated}");
                }

                if (!string.IsNullOrEmpty(item.url))
                    sb.AppendLine($"      -> {item.url}");

                sb.AppendLine();
            }
        }

        // === TOPIC BREAKDOWN: Group remaining items by topic ===
        var byTopic = items
            .OrderByDescending(i => i.relevance)
            .GroupBy(x => x.topic)
            .OrderByDescending(g => g.Max(i => i.relevance));

        sb.AppendLine("## By Topic");
        sb.AppendLine();

        foreach (var group in byTopic)
        {
            var topicTitle = char.ToUpper(group.Key[0]) + group.Key[1..];
            var avgSentiment = group.Average(g => g.sentiment);
            var topRelevance = group.Max(g => g.relevance);
            var sentimentIndicator = avgSentiment switch
            {
                > 0.1f => "+",
                < -0.1f => "-",
                _ => "~"
            };
            sb.AppendLine($"### {topicTitle} [{sentimentIndicator}] ({group.Count()} items, top relevance: {topRelevance:F2})");

            foreach (var item in group.OrderByDescending(i => i.relevance).Take(5))
            {
                var pct = items.Max(i => i.relevance) > 0
                    ? (int)(item.relevance / items.Max(i => i.relevance) * 100)
                    : 0;
                var sentimentIcon = item.sentiment switch
                {
                    > 0.15f => "[+]",
                    < -0.15f => "[-]",
                    _ => "[~]"
                };

                if (!string.IsNullOrEmpty(item.url))
                    sb.AppendLine($"- {sentimentIcon} {pct}% {item.title}");
                else
                    sb.AppendLine($"- {sentimentIcon} {pct}% {item.title}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extract key themes: topic distribution + cross-article terms from content.
    /// Cheap computation (no ML) — pure term frequency analysis.
    /// </summary>
    internal static (
        List<(string topic, int count)> topics,
        List<(string term, int articles)> terms)
        ExtractKeyThemes(
            List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
            List<ContentItem> contentItems)
    {
        // Topic distribution from analyzed items
        var topics = analyzedItems
            .GroupBy(i => i.topic, StringComparer.OrdinalIgnoreCase)
            .Select(g => (topic: g.Key, count: g.Count()))
            .OrderByDescending(t => t.count)
            .Take(8)
            .ToList();

        // Cross-article term extraction from content
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "from", "as", "is", "was", "are", "were", "be",
            "been", "being", "have", "has", "had", "do", "does", "did", "will",
            "would", "could", "should", "may", "might", "shall", "can", "need",
            "it", "its", "this", "that", "these", "those", "he", "she", "they",
            "we", "you", "i", "me", "my", "your", "his", "her", "our", "their",
            "not", "no", "nor", "so", "if", "then", "than", "too", "very",
            "just", "about", "also", "more", "most", "some", "any", "all",
            "each", "every", "both", "few", "many", "much", "such", "own",
            "same", "other", "new", "old", "first", "last", "long", "great",
            "little", "right", "big", "high", "small", "large", "next", "early",
            "young", "important", "public", "bad", "good", "best", "said", "says",
            "like", "well", "back", "even", "still", "way", "take", "come",
            "make", "know", "get", "got", "go", "see", "look", "think", "give",
            "use", "find", "tell", "ask", "work", "seem", "feel", "try", "leave",
            "call", "keep", "let", "put", "show", "turn", "start", "run", "move",
            "play", "live", "believe", "bring", "happen", "write", "provide",
            "sit", "stand", "lose", "pay", "meet", "include", "continue",
            "set", "learn", "change", "lead", "understand", "watch", "follow",
            "stop", "create", "speak", "read", "add", "spend", "grow", "open",
            "walk", "win", "offer", "remember", "love", "consider", "appear",
            "buy", "wait", "serve", "die", "send", "expect", "build", "stay",
            "fall", "cut", "reach", "kill", "remain", "suggest", "raise", "pass",
            "sell", "require", "report", "decide", "pull", "develop", "report",
            "one", "two", "three", "four", "five", "six", "seven", "eight",
            "nine", "ten", "year", "years", "day", "days", "time", "week",
            "month", "per", "people", "world", "part", "group", "number",
            "fact", "however", "while", "after", "before", "between", "under",
            "since", "during", "through", "against", "into", "over", "only",
            "now", "where", "when", "what", "which", "who", "how", "why",
            "here", "there", "because", "although", "though", "whether",
            "already", "yet", "never", "always", "often", "ever", "really",
            "according", "around", "without", "within", "across", "along",
            "using", "used", "says", "also", "been", "being", "having",
            "going", "made", "making", "getting", "doing", "done", "called",
            "based", "including", "according", "among", "became", "become",
            "another", "several", "less", "given", "among",
            "news", "articles", "article", "latest", "generated", "content",
            "page", "pages", "site", "website", "click", "link", "links",
            "share", "comment", "comments", "posted", "updated", "subscribe",
            "sign", "login", "search", "menu", "home", "about", "contact",
            "section", "topics", "topic", "read", "related", "more", "view"
        };

        // Count terms that appear in 2+ articles (document frequency)
        var termDocFreq = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var topItems = contentItems
            .OrderByDescending(i => i.RelevanceScore)
            .Take(25)
            .ToList();

        foreach (var item in topItems)
        {
            var text = $"{item.Title} {item.Content ?? ""}";
            var tokens = text
                .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\'', '—', '–', '-', '/', '\\'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length >= 4 && t.Length <= 30
                            && !stopWords.Contains(t) && !int.TryParse(t, out _)
                            && !t.StartsWith("http") && !t.Contains("www.")
                            && !t.Contains(".com") && !t.Contains(".org") && !t.Contains(".net"))
                .Distinct()
                .ToList();

            foreach (var token in tokens)
            {
                if (!termDocFreq.ContainsKey(token))
                    termDocFreq[token] = [];
                termDocFreq[token].Add(item.Id);
            }
        }

        // Terms appearing across 3+ articles are key themes
        var terms = termDocFreq
            .Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key)
            .Take(12)
            .Select(kv => (term: kv.Key, articles: kv.Value.Count))
            .ToList();

        return (topics, terms);
    }

    /// <summary>
    /// Compute in-corpus link authority: count how many articles in the corpus
    /// link to each other article's URL (a "silly PageRank").
    /// </summary>
    private static Dictionary<string, int> ComputeInCorpusLinkAuthority(List<ContentItem> items)
    {
        // Build set of all article URLs in corpus (normalized)
        var corpusUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Url))
                corpusUrls.Add(NormalizeUrlForAuthority(item.Url));
        }

        // Count incoming links from LinkedPages
        var inLinkCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var linked in item.LinkedPages)
            {
                var normalizedLinked = NormalizeUrlForAuthority(linked.Url);
                if (corpusUrls.Contains(normalizedLinked))
                {
                    inLinkCounts.TryGetValue(normalizedLinked, out var count);
                    inLinkCounts[normalizedLinked] = count + 1;
                }
            }
        }

        return inLinkCounts;
    }

    private static string NormalizeUrlForAuthority(string url) =>
        url.Split('?')[0].TrimEnd('/').ToLowerInvariant();
}

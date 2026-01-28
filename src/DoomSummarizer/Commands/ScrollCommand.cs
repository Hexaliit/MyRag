using System.ComponentModel;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using DoomSummarizer.Services.LongFormGeneration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed partial class ScrollCommand : AsyncCommand<ScrollCommand.Settings>
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

        [CommandOption("-n|--name")]
        [Description("Query a named knowledge base collection (implies --local). Use 'show' command to list collections.")]
        public string? Name { get; init; }

        [CommandOption("--debug-pipeline|--debug")]
        [Description("Show detailed pipeline diagnostics: RRF component scores, discards, salience breakdown")]
        public bool DebugPipeline { get; init; }

        [CommandOption("--list-templates")]
        [Description("List available output templates")]
        public bool ListTemplates { get; init; }

        [CommandOption("--email")]
        [Description("Send digest via email (configure with email section in config)")]
        public bool SendEmail { get; init; }

        [CommandOption("--email-to")]
        [Description("Override email recipient(s), comma-separated")]
        public string? EmailTo { get; init; }

        [CommandOption("--briefing")]
        [Description("Show evidence briefing panel with themes, entities, and coverage metrics")]
        public bool Briefing { get; init; }

        [CommandOption("--clear-storage")]
        [Description("Delete all cached data (segments, queries, entities) and exit")]
        public bool ClearStorage { get; init; }
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

        // Handle --clear-storage: wipe all cached data and exit
        if (settings.ClearStorage)
        {
            await storage.ClearAllAsync();

            // Also clear the DuckDB vector store (knowledge graph, HNSW embeddings)
            var vectorDbPath = ConfigService.GetVectorDbPath();
            if (File.Exists(vectorDbPath))
            {
                try
                {
                    await using var vs = new DuckDbVectorStore(vectorDbPath);
                    await vs.InitializeAsync();
                    await vs.ClearAllAsync();
                    AnsiConsole.MarkupLine("[green]Vector store cleared (knowledge graph, HNSW embeddings)[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Could not clear vector store: {Markup.Escape(ex.Message)}[/]");
                }
            }

            AnsiConsole.MarkupLine("[green]All stored data cleared (segments, queries, entities, circuit state, API usage, vectors)[/]");
            return 0;
        }

        // Auto-backfill FTS5 index if empty (one-time migration for existing KB items)
        if (await storage.IsFtsIndexEmptyAsync())
        {
            await BackfillFtsIndexAsync(storage, settings.Quiet);
        }

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

        // Initialize API key service, resilience pipeline, and budget tracker
        var apiKeys = ApiKeyService.Load(config);
        ApiRateLimiter.Configure(apiKeys);
        await using var apiBudget = new ApiBudgetService(config.ApiBudget, apiKeys, dbPath);
        await apiBudget.InitializeAsync();

        // Persistent circuit breaker — survives restarts, smart retry by failure type
        await using var circuitBreaker = new CircuitBreakerService(dbPath);
        await circuitBreaker.InitializeAsync();
        ApiRateLimiter.SetCircuitBreaker(circuitBreaker);

        if (settings.DebugPipeline)
            circuitBreaker.PrintCircuitStatus();

        // Wire cloud LLM providers (OpenAI/Anthropic) through the router
        // When available, OllamaService delegates generate calls through the router
        // with budget enforcement and automatic fallback to local Ollama
        var llmRouter = await LlmRouter.BuildAsync(config.Ollama, apiKeys, apiBudget, circuitBreaker, cancellationToken);
        ollama.Router = llmRouter;

        // Auto-setup: download ONNX models if not present (first run)
        await embedding.EnsureReadyAsync(msg =>
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
        });

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

        // Status helper: overwrites the previous status line to keep output compact.
        // Only the latest status is visible at any time.
        var hasStatusLine = false;
        void WriteStatus(string markup)
        {
            if (settings.Quiet) return;
            if (hasStatusLine)
                Console.Write("\x1b[1A\x1b[2K"); // Move up one line, clear it
            AnsiConsole.MarkupLine(markup);
            hasStatusLine = true;
        }

        WriteStatus($"[grey]LLM: {Markup.Escape(llmRouter.StatusDescription)}[/]");

        // NER preprocessing: extract entities from query BEFORE the LLM sentinel
        // This gives us structured search filters, cached segment lookups, and URL dedup
        QueryNerContext? nerContext = null;
        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            nerContext = await QueryPreprocessor.PreprocessAsync(
                settings.Prompt, embedding, storage, cancellationToken);

            if (nerContext.HasEntities)
            {
                var entityStr = string.Join(", ", nerContext.Entities
                    .Select(e => $"{e.Text} ({e.Type})"));
                WriteStatus($"[grey]NER: {Markup.Escape(entityStr)}[/]");
            }
        }

        // Interpret the prompt if provided
        // Skip sentinel interpretation in --name mode (KB query doesn't need web source detection)
        InterpretedPrompt? interpreted = null;
        var vibe = settings.Vibe;
        var isNamedKbQuery = !string.IsNullOrWhiteSpace(settings.Name);

        if (!string.IsNullOrEmpty(settings.Prompt) && !isNamedKbQuery)
        {
            WriteStatus($"[grey]Interpreting: {Markup.Escape(settings.Prompt)}[/]");

            var interpreter = new PromptInterpreter(ollama, embedding);
            interpreted = await interpreter.InterpretAsync(settings.Prompt, nerContext);

            // Use interpreted vibe unless explicitly overridden
            if (settings.Vibe == "neutral" && interpreted.Vibe != "neutral")
                vibe = interpreted.Vibe;

            var sourcesStr = string.Join(", ", interpreted.Sources
                .Concat(interpreted.Websites)
                .Concat(interpreted.SearchQueries.Select(q => $"search:{q}")));
            WriteStatus($"[grey]Detected: sources=[[{Markup.Escape(sourcesStr)}]], vibe={vibe}[/]");
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
        if (!ollamaAvailable)
            WriteStatus("[yellow]Warning: Ollama not available. Summaries will be limited.[/]");

        // Query feedback: check for similar recent query to reuse cached segments
        var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";
        float[]? earlyQueryEmbedding = null;
        QueryMatch? cachedQuery = null;
        var useCachedSegments = false;

        if (!settings.Force && !settings.LocalOnly && string.IsNullOrWhiteSpace(settings.Name) && !string.IsNullOrWhiteSpace(queryText))
        {
            earlyQueryEmbedding = embedding.Embed(queryText);
            cachedQuery = await storage.FindSimilarQueryAsync(earlyQueryEmbedding, threshold: 0.97);
            if (cachedQuery != null)
            {
                useCachedSegments = true;
                var ageMin = (int)(DateTimeOffset.UtcNow - cachedQuery.IssuedAt).TotalMinutes;
                WriteStatus($"[grey]Reusing {cachedQuery.ItemIds.Count} segments ({cachedQuery.Similarity:F2} match, {ageMin}m ago)[/]");
            }
        }

        // Clear the status line before Progress takes over rendering
        if (hasStatusLine)
            Console.Write("\x1b[1A\x1b[2K");

        var items = new List<ContentItem>();
        var uniqueItems = new List<ContentItem>();

        // Rendering state — hoisted so console output happens after progress bars are gone
        var analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();
        var finalSummary = "";
        var template = "default";
        var isBlogTemplate = false;
        DigestData? templateData = null;
        var allEntities = new List<NerEntity>();
        var articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
        var extractEntities = false;
        var linkCacheHits = 0;
        var linksSkippedByRelevance = 0;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Stage 1: Fetch content (or load from knowledge base)
                // --name implies --local mode (query a named KB collection)
                var isLocalMode = settings.LocalOnly || !string.IsNullOrWhiteSpace(settings.Name);
                var fetchTask = ctx.AddTask(
                    isLocalMode ? "[cyan]Loading from knowledge base[/]" : "[cyan]Fetching content[/]",
                    maxValue: 100);

                // --local / --name mode: skip ALL fetching, query stored knowledge base only
                if (isLocalMode)
                {
                    var localQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";

                    // Derive source filter: --name takes priority, then --source crawl:xxx
                    var sourceFilter = !string.IsNullOrWhiteSpace(settings.Name)
                        ? $"crawl:{settings.Name}"
                        : settings.Sources?.FirstOrDefault(s =>
                            s.StartsWith("crawl:", StringComparison.OrdinalIgnoreCase));

                    var collectionLabel = sourceFilter ?? "all";
                    List<ContentItem> localItems;

                    if (!string.IsNullOrWhiteSpace(localQuery))
                    {
                        fetchTask.Value = 20;

                        // Layer 1: FTS5 pre-filter (deterministic SQL keyword match)
                        var candidateIds = await storage.FtsPreFilterAsync(
                            localQuery, source: sourceFilter, limit: settings.Limit * 3);
                        fetchTask.Value = 40;

                        if (candidateIds.Count > 0)
                        {
                            // Load full items for FTS5 candidates
                            localItems = await storage.LoadItemsByIdsAsync(candidateIds);
                            fetchTask.Description = $"[cyan]FTS5: {candidateIds.Count} keyword candidates[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]FTS5 pre-filter: {candidateIds.Count} candidates from keyword match[/]");
                        }
                        else
                        {
                            // FTS5 found nothing — fall back to embedding search with raised threshold
                            var queryEmbed = embedding.Embed(localQuery);
                            var similarStored = await storage.FindSimilarAsync(
                                queryEmbed, limit: settings.Limit * 2, threshold: 0.25, source: sourceFilter);
                            localItems = similarStored
                                .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                                .Select(s => s.ToContentItem())
                                .ToList();

                            fetchTask.Description = $"[cyan]Embedding fallback: {localItems.Count} items[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]FTS5 empty — embedding fallback: {localItems.Count} items (threshold 0.25)[/]");
                        }
                        fetchTask.Value = 70;

                        // Filter out items without any content
                        localItems = localItems
                            .Where(i => !string.IsNullOrEmpty(i.Summary) || !string.IsNullOrEmpty(i.Title))
                            .Take(settings.Limit * 2)
                            .ToList();
                    }
                    else
                    {
                        // No query: return most recent from the collection
                        var storedLocal = sourceFilter != null
                            ? await storage.GetRecentItemsAsync(days: 365, source: sourceFilter)
                            : await storage.GetRecentItemsAsync(days: 30);
                        fetchTask.Value = 70;

                        localItems = storedLocal
                            .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                            .Select(s => s.ToContentItem())
                            .OrderByDescending(i => i.FetchedAt)
                            .Take(settings.Limit)
                            .ToList();
                    }

                    items.AddRange(localItems);
                    fetchTask.Value = 100;
                    fetchTask.Description = $"[green]Loaded {items.Count} items from KB '{Markup.Escape(collectionLabel)}'[/]";

                    fetchTask.Description = $"[cyan]KB: {items.Count} items matched[/]";
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]KB query ({Markup.Escape(collectionLabel)}): {items.Count} items matched[/]");
                }

                // Segment reuse: load cached items from a similar recent query
                if (!isLocalMode && useCachedSegments && cachedQuery != null)
                {
                    var cachedStored = await storage.GetItemsByIdsAsync(cachedQuery.ItemIds);
                    var cachedItems = cachedStored
                        .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                        .Select(s => s.ToContentItem())
                        .ToList();

                    // Relevance gate: verify cached segments actually relate to the query
                    // This prevents reusing stale/irrelevant results from a previous identical query
                    if (earlyQueryEmbedding != null && cachedItems.Count > 0)
                    {
                        var withEmbeddings = cachedItems.Where(i => i.Embedding != null).ToList();
                        if (withEmbeddings.Count > 0)
                        {
                            var similarities = withEmbeddings
                                .Select(i => EmbeddingService.CosineSimilarity(earlyQueryEmbedding, i.Embedding!))
                                .OrderByDescending(s => s)
                                .ToList();

                            // Use the best-of-top-5 as the relevance signal
                            var topRelevance = similarities.Take(5).Average();

                            if (topRelevance < 0.25f)
                            {
                                // Cached segments are irrelevant — skip cache, fetch fresh
                                useCachedSegments = false;
                                if (!settings.Quiet)
                                    AnsiConsole.MarkupLine($"[yellow]Cached segments are irrelevant (best relevance: {topRelevance:F2}) — fetching fresh results[/]");
                            }
                        }
                    }

                    if (useCachedSegments)
                    {
                        items.AddRange(cachedItems);
                        fetchTask.Value = 100;
                        fetchTask.Description = $"[green]Reused {items.Count} cached segments (skipped fetching)[/]";
                    }
                }

                // Detect query type early — used for roundup date-gating inside fetch mode
                // AND for adaptive RRF weights in Stage 2.5 (outside the fetch block)
                var earlyQueryType = QueryTypeDetector.Detect(interpreted?.RawPrompt ?? settings.Prompt, interpreted?.SentinelIntent);

                if (!isLocalMode && !useCachedSegments)
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
                                return await new GoogleSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            if (apiKeys.IsAvailable("brave_search"))
                                return await new BraveSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            if (apiKeys.IsAvailable("serper"))
                                return await new SerperSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                    .SearchAsync(qualifiedQuery, searchLimit);
                            if (apiKeys.IsAvailable("tavily"))
                                return await new TavilySearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
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
                            await new GoogleSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(qualifiedQuery, perSourceLimit * 2)));
                    }
                    else if (src.StartsWith("gplaces:") || src == "gplaces")
                    {
                        var query = src == "gplaces"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[8..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new GooglePlacesService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("brave:") || src.StartsWith("brave_search:") || src is "brave" or "brave_search")
                    {
                        var query = src is "brave" or "brave_search"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[(source.IndexOf(':') + 1)..];
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        fetchTasks.Add(Task.Run(async () =>
                            await new BraveSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(qualifiedQuery, perSourceLimit * 2)));
                    }
                    else if (src.StartsWith("bravenews:") || src.StartsWith("brave_news:") || src is "bravenews" or "brave_news")
                    {
                        var query = src is "bravenews" or "brave_news"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[(source.IndexOf(':') + 1)..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new BraveSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(query, perSourceLimit, newsOnly: true)));
                    }
                    else if (src.StartsWith("serper:") || src == "serper")
                    {
                        var query = src == "serper"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[7..];
                        var qualifiedQuery = QualifySearchQuery(query, vibe);
                        fetchTasks.Add(Task.Run(async () =>
                            await new SerperSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(qualifiedQuery, perSourceLimit * 2)));
                    }
                    else if (src.StartsWith("serpernews:") || src.StartsWith("serper_news:") || src is "serpernews" or "serper_news")
                    {
                        var query = src is "serpernews" or "serper_news"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[(source.IndexOf(':') + 1)..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new SerperSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(query, perSourceLimit, newsOnly: true)));
                    }
                    else if (src.StartsWith("tavily:") || src == "tavily")
                    {
                        var query = src == "tavily"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[7..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new TavilySearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("newsapi:") || src.StartsWith("news_api:") || src is "newsapi" or "news_api")
                    {
                        var query = src is "newsapi" or "news_api"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[(source.IndexOf(':') + 1)..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new NewsApiService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("newsdata:") || src.StartsWith("news_data:") || src is "newsdata" or "news_data")
                    {
                        var query = src is "newsdata" or "news_data"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[(source.IndexOf(':') + 1)..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new NewsDataService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                .SearchAsync(query, perSourceLimit)));
                    }
                    else if (src.StartsWith("jina:") || src == "jina")
                    {
                        var query = src == "jina"
                            ? interpreted?.RawPrompt ?? settings.Prompt ?? ""
                            : source[5..];
                        fetchTasks.Add(Task.Run(async () =>
                            await new JinaSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
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
                                await new BraveSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                    .SearchAsync(fallbackQuery, perSourceLimit * 2)));
                        }
                        else if (apiKeys.IsAvailable("serper"))
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new SerperSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                    .SearchAsync(fallbackQuery, perSourceLimit * 2)));
                        }
                        else if (apiKeys.IsAvailable("tavily"))
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new TavilySearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                    .SearchAsync(fallbackQuery, perSourceLimit)));
                        }
                        else if (apiKeys.HasGoogleSearch)
                        {
                            fallbackSources.Add(Task.Run(async () =>
                                await new GoogleSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
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
                                await new NewsApiService(httpClient, apiKeys, apiBudget, circuitBreaker)
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
                        if (fallbackCount > 0)
                            fetchTask.Description = $"[cyan]Fallback: +{fallbackCount} items[/]";
                        if (settings.DebugPipeline && fallbackCount > 0)
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
                            fetchTask.Description = $"[cyan]Topic filter: skipped (too few)[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} (too aggressive, skipping)[/]");
                            filtered = items;
                        }
                        else
                        {
                            fetchTask.Description = $"[cyan]Topic filter: {filtered.Count} items[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items (relaxed)[/]");
                        }
                    }
                    else if (filtered.Count < preFilterCount)
                    {
                        fetchTask.Description = $"[cyan]Topic filter: {filtered.Count} items[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items[/]");
                    }

                    items = filtered;
                }

                // Roundup intent: date-gate and penalize topic drift
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

                        {
                            var freshCount = items.Count(i =>
                                (DateTimeOffset.UtcNow - i.CreatedAt) <= maxAge);
                            fetchTask.Description = $"[cyan]Date-gate: {freshCount}/{items.Count} fresh[/]";
                            if (settings.DebugPipeline)
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
                        fetchTask.Description = $"[cyan]NER cache: +{nerCachedItems.Count} items[/]";
                        if (settings.DebugPipeline)
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

                    if (uniqueItems.Count < preFilterCount)
                        fetchTask.Description = $"[cyan]Source filter: {uniqueItems.Count} items[/]";
                    if (settings.DebugPipeline && uniqueItems.Count < preFilterCount)
                        AnsiConsole.MarkupLine($"[grey]Source filter: {preFilterCount} → {uniqueItems.Count} items[/]");
                }

                // Stage 2.2: FTS5 KB enrichment (web queries only)
                // Check if stored KB items match the query — merge them into web results
                if (!isLocalMode && uniqueItems.Count > 0)
                {
                    var enrichQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                    if (!string.IsNullOrWhiteSpace(enrichQuery))
                    {
                        var storedCandidateIds = await storage.FtsPreFilterAsync(enrichQuery, limit: 10);
                        if (storedCandidateIds.Count > 0)
                        {
                            var storedItems = await storage.LoadItemsByIdsAsync(storedCandidateIds);
                            var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                            var existingUrls2 = new HashSet<string>(
                                uniqueItems.Where(i => !string.IsNullOrEmpty(i.Url))
                                    .Select(i => i.Url!.Split('?')[0].TrimEnd('/').ToLowerInvariant()),
                                StringComparer.OrdinalIgnoreCase);
                            var newFromKb = storedItems.Where(s =>
                                !existingIds.Contains(s.Id) &&
                                (string.IsNullOrEmpty(s.Url) || !existingUrls2.Contains(s.Url.Split('?')[0].TrimEnd('/').ToLowerInvariant())))
                                .ToList();
                            if (newFromKb.Count > 0)
                            {
                                uniqueItems.AddRange(newFromKb);
                                fetchTask.Description = $"[cyan]KB enrichment: +{newFromKb.Count} items[/]";
                                if (settings.DebugPipeline)
                                    AnsiConsole.MarkupLine($"[grey]FTS5 KB enrichment: +{newFromKb.Count} stored items merged[/]");
                            }
                        }
                    }
                }

                // Stage 2.5: Embedding computation + two-phase relevance scoring with RRF
                // Use query-type-adaptive weights: roundups boost freshness, explainers boost authority
                var scorer = RelevanceScorer.ForQueryType(earlyQueryType);
                var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";

                // Augment BM25 query with sentinel-expanded search terms.
                // The sentinel LLM expands abbreviations (e.g. "SNL" → "Saturday Night Live"),
                // fixes spelling, and adds synonyms. These extra terms improve BM25F vocabulary
                // coverage without affecting the embedding-based semantic similarity signal.
                var bm25Query = queryText;
                if (interpreted?.SearchQueries?.Count > 0)
                {
                    var extraTerms = string.Join(" ", interpreted.SearchQueries);
                    bm25Query = $"{queryText} {extraTerms}";
                    fetchTask.Description = $"[cyan]BM25: +{interpreted.SearchQueries.Count} terms[/]";
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]BM25 expanded: +{interpreted.SearchQueries.Count} sentinel terms[/]");
                }

                // Compute keyword profiles for items that don't have them yet (web-fetched items)
                foreach (var item in uniqueItems)
                {
                    if (string.IsNullOrEmpty(item.Keywords))
                    {
                        var profile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
                        item.Keywords = profile.KeywordsText;
                    }
                }

                // Compute embeddings for ALL items BEFORE scoring
                // This enables semantic matching in Phase 1 (e.g. "pharmaceutical" matches "drug pricing")
                // without needing synonym dictionaries — embeddings capture semantic similarity dynamically
                float[]? queryEmbedding = null;
                float[]? vibeEmbedding = null;
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    // ONNX InferenceSession.Run() is thread-safe — parallelize embedding computation
                    var itemsNeedingEmbedding = uniqueItems.Where(i => i.Embedding == null).ToList();
                    if (itemsNeedingEmbedding.Count > 0)
                    {
                        Parallel.ForEach(itemsNeedingEmbedding,
                            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) },
                            item =>
                            {
                                var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                                item.Embedding = embedding.Embed(textToEmbed);
                            });
                    }

                    queryEmbedding = embedding.Embed(queryText);
                    var vibeText = GetVibeRepresentativeText(vibe);
                    vibeEmbedding = vibe != "neutral" ? embedding.Embed(vibeText) : null;

                    // Quality anchors: detect clickbait vs substantive content
                    var highQualityAnchor = embedding.Embed(RelevanceScorer.HighQualityAnchorText);
                    var lowQualityAnchor = embedding.Embed(RelevanceScorer.LowQualityAnchorText);
                    scorer = scorer.WithQualityAnchors(highQualityAnchor, lowQualityAnchor);
                }

                // Load global keyword corpus for proper IDF computation
                // (IDF from full corpus is more reliable than batch-only IDF)
                Dictionary<string, int>? globalCorpus = null;
                int? globalCorpusSize = null;
                try
                {
                    globalCorpus = await storage.GetKeywordCorpusAsync();
                    if (globalCorpus.Count > 0)
                    {
                        globalCorpusSize = await storage.GetKeywordCorpusSizeAsync();
                        fetchTask.Description = $"[cyan]IDF: {globalCorpus.Count} terms[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]Global IDF: {globalCorpus.Count} terms, {globalCorpusSize} docs[/]");
                    }
                    else
                    {
                        globalCorpus = null; // Fall back to batch IDF
                    }
                }
                catch
                {
                    // Keyword corpus not yet populated — fall back to batch IDF
                }

                // Phase 1: Fast discard using BM25 + freshness + authority + semantic similarity
                if (!string.IsNullOrWhiteSpace(queryText) && uniqueItems.Count > 5)
                {
                    var preScoreCount = uniqueItems.Count;

                    // Capture pre-discard scores for debug output
                    List<(ContentItem item, double bm25, double freshness, double authority, double qSim)>? phase1Debug = null;
                    if (settings.DebugPipeline)
                    {
                        var qt = RelevanceScorer.Tokenize(bm25Query);
                        var (idf, avgDocLen) = RelevanceScorer.BuildCorpusStats(uniqueItems, globalCorpus, globalCorpusSize);
                        var authLookup = RelevanceScorer.ComputeAuthorityScores(uniqueItems)
                            .ToDictionary(x => x.item.Id, x => x.score);
                        phase1Debug = uniqueItems.Select(i => (
                            item: i,
                            bm25: (double)RelevanceScorer.BM25FScore(i, qt, idf, avgDocLen),
                            freshness: RelevanceScorer.ComputeFreshness(i),
                            authority: authLookup.GetValueOrDefault(i.Id, 0.3),
                            qSim: i.Embedding != null && queryEmbedding != null
                                ? (double)EmbeddingService.CosineSimilarity(i.Embedding, queryEmbedding) : 0.0
                        )).ToList();
                    }

                    uniqueItems = scorer.ScoreFast(uniqueItems, bm25Query, discardRatio: 0.25, queryEmbedding: queryEmbedding,
                        globalCorpus: globalCorpus, globalCorpusSize: globalCorpusSize);

                    if (settings.DebugPipeline && phase1Debug != null)
                    {
                        // Show which items were kept vs discarded
                        var keptIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                        AnsiConsole.WriteLine();
                        var table = new Table()
                            .Title("[bold yellow]Phase 1: Scoring (BM25F + Freshness + Authority + Semantic)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]Status[/]")
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]BM25F[/]")
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
                        AnsiConsole.MarkupLine($"[grey]Query type: {earlyQueryType} | BM25 tokens: {string.Join(", ", RelevanceScorer.Tokenize(bm25Query))}[/]");
                    }

                    if (uniqueItems.Count < preScoreCount)
                        fetchTask.Description = $"[cyan]Relevance: {uniqueItems.Count} items[/]";
                    if (settings.DebugPipeline && uniqueItems.Count < preScoreCount)
                        AnsiConsole.MarkupLine($"[grey]Fast relevance filter: {preScoreCount} → {uniqueItems.Count} items (discarded low-salience)[/]");
                }

                // PRF centroid refinement: blend query embedding with top-K results from Phase 1.
                // This captures the "semantic neighborhood" of relevant results, helping with
                // vocabulary mismatch (e.g. query "drug pricing" finds "pharmaceutical costs").
                float[]? refinedQueryEmbedding = queryEmbedding;
                if (queryEmbedding != null && uniqueItems.Count >= 5)
                {
                    refinedQueryEmbedding = RelevanceScorer.ComputePRFCentroid(uniqueItems, queryEmbedding);
                    if (refinedQueryEmbedding != queryEmbedding)
                        fetchTask.Description = $"[cyan]PRF: refined from top-{Math.Min(5, uniqueItems.Count)}[/]";
                    if (settings.DebugPipeline && refinedQueryEmbedding != queryEmbedding)
                        AnsiConsole.MarkupLine($"[grey]PRF: refined query embedding from top-{Math.Min(5, uniqueItems.Count)} results[/]");
                }

                // Phase 2: Full RRF with vibe alignment added (embeddings already computed)
                if (!string.IsNullOrWhiteSpace(queryText) && refinedQueryEmbedding != null)
                {
                    uniqueItems = scorer.ScoreFull(uniqueItems, bm25Query, refinedQueryEmbedding, vibeEmbedding,
                        globalCorpus: globalCorpus, globalCorpusSize: globalCorpusSize);

                    if (settings.DebugPipeline)
                    {
                        // Recompute individual Phase 2 signals for debug display
                        var qt = RelevanceScorer.Tokenize(bm25Query);
                        var (idf2, avgDocLen2) = RelevanceScorer.BuildCorpusStats(uniqueItems, globalCorpus, globalCorpusSize);
                        var authLookup2 = RelevanceScorer.ComputeAuthorityScores(uniqueItems)
                            .ToDictionary(x => x.item.Id, x => x.score);

                        AnsiConsole.WriteLine();
                        var table = new Table()
                            .Title("[bold yellow]Phase 2: Full RRF (+ Query Similarity + Vibe Alignment)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]#[/]")
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]BM25F[/]")
                            .AddColumn("[cyan]Fresh[/]")
                            .AddColumn("[cyan]Auth[/]")
                            .AddColumn("[cyan]QSim[/]")
                            .AddColumn("[cyan]Vibe[/]")
                            .AddColumn("[cyan]RRF[/]")
                            .AddColumn("[cyan]Title[/]");

                        var rank = 1;
                        foreach (var item in uniqueItems.Take(25))
                        {
                            var bm25 = RelevanceScorer.BM25FScore(item, qt, idf2, avgDocLen2);
                            var fresh = RelevanceScorer.ComputeFreshness(item);
                            var auth = authLookup2.GetValueOrDefault(item.Id, 0.3);
                            var qSim = item.Embedding != null && refinedQueryEmbedding != null
                                ? EmbeddingService.CosineSimilarity(item.Embedding, refinedQueryEmbedding) : 0f;
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

                    fetchTask.Description = $"[cyan]RRF ranked: {uniqueItems.Count} items[/]";
                    if (settings.DebugPipeline)
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
                    if (weightedCount > 0)
                        fetchTask.Description = $"[cyan]Src weights: {weightedCount} adjusted[/]";
                    if (settings.DebugPipeline && weightedCount > 0)
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

                            fetchTask.Description = $"[cyan]LFU: {lfuAdjusted} items decayed[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]LFU diversity: {lfuAdjusted} frequently-seen items decayed[/]");
                        }
                    }
                }

                // Stage 2.5c: One-hop link following for richer context
                linkCacheHits = 0;
                linksSkippedByRelevance = 0;
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

                    if (inLinkCounts.Values.Any(c => c > 0))
                    {
                        var boostedCount = inLinkCounts.Count(kv => kv.Value > 0);
                        fetchTask.Description = $"[cyan]PageRank: {boostedCount} boosted[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]In-corpus PageRank: {boostedCount} items boosted by cross-references[/]");
                    }
                }

                // Evidence sufficiency check: if top items are irrelevant, re-search with focused queries
                if (queryEmbedding != null && uniqueItems.Count > 0 && !isLocalMode)
                {
                    var topItems = uniqueItems.Take(5).Where(i => i.Embedding != null).ToList();
                    if (topItems.Count > 0)
                    {
                        var avgRelevance = topItems
                            .Select(i => (double)EmbeddingService.CosineSimilarity(queryEmbedding, i.Embedding!))
                            .Average();

                        if (avgRelevance < 0.25)
                        {
                            if (!settings.Quiet)
                                AnsiConsole.MarkupLine(
                                    $"[yellow]Evidence gap detected (top-5 relevance: {avgRelevance:F2}) — running targeted re-search[/]");

                            // Re-search with the raw query directly through available search APIs
                            var reSearchQuery = queryText;
                            var reSearchResults = new List<ContentItem>();
                            var reSearchTasks = new List<Task<List<ContentItem>>>();

                            if (apiKeys.IsAvailable("brave_search"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new BraveSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));
                            if (apiKeys.IsAvailable("serper"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new SerperSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));
                            if (apiKeys.IsAvailable("tavily"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new TavilySearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));
                            if (apiKeys.IsAvailable("jina"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new JinaSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 5)));
                            if (reSearchTasks.Count == 0)
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new DuckDuckGoSearch(httpClient)
                                        .SearchAsync(reSearchQuery, 10)));

                            var reSearchBatches = await Task.WhenAll(reSearchTasks);
                            foreach (var batch in reSearchBatches)
                                reSearchResults.AddRange(batch);

                            if (reSearchResults.Count > 0)
                            {
                                // Embed and deduplicate new results
                                var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                                var existingUrls = new HashSet<string>(
                                    uniqueItems.Where(i => !string.IsNullOrEmpty(i.Url))
                                        .Select(i => i.Url!.Split('?')[0].TrimEnd('/').ToLowerInvariant()),
                                    StringComparer.OrdinalIgnoreCase);
                                var existingTitles = new HashSet<string>(
                                    uniqueItems.Select(i => i.Title.ToLowerInvariant().Trim()),
                                    StringComparer.OrdinalIgnoreCase);
                                var newItems = reSearchResults.Where(i =>
                                    !existingIds.Contains(i.Id) &&
                                    (string.IsNullOrEmpty(i.Url) || !existingUrls.Contains(i.Url.Split('?')[0].TrimEnd('/').ToLowerInvariant())) &&
                                    !existingTitles.Contains(i.Title.ToLowerInvariant().Trim()))
                                    .ToList();

                                // ONNX InferenceSession.Run() is thread-safe — parallelize
                                Parallel.ForEach(newItems,
                                    new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) },
                                    item =>
                                    {
                                        var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                                        item.Embedding = embedding.Embed(textToEmbed);
                                    });

                                // Merge and re-score
                                uniqueItems.AddRange(newItems);
                                uniqueItems = scorer.ScoreFull(uniqueItems, bm25Query, queryEmbedding, vibeEmbedding,
                                    globalCorpus: globalCorpus, globalCorpusSize: globalCorpusSize);

                                fetchTask.Description = $"[cyan]Re-search: +{newItems.Count} items[/]";
                                if (settings.DebugPipeline)
                                    AnsiConsole.MarkupLine(
                                        $"[grey]Re-search: {newItems.Count} new items merged, {uniqueItems.Count} total[/]");
                            }
                        }
                    }
                }

                // Stage 3: Deterministic signal analysis — no LLM
                // Segments, sentiment, topic all computed via ONNX embeddings and article processing.
                // The LLM is reserved for Stage 4 (synthesis) only.
                analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();

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

                    if (alreadyAnalyzed.Count > 0)
                        fetchTask.Description = $"[cyan]Cached: {alreadyAnalyzed.Count} items[/]";
                    if (alreadyAnalyzed.Count > 0 && settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]Using cached analyses for {alreadyAnalyzed.Count} previously processed items[/]");

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
                        // ONNX InferenceSession.Run() is thread-safe — parallelize article processing
                        using var articleProcessor = new ArticleProcessor();

                        var parallelOpts = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
                        };

                        await Parallel.ForEachAsync(needsAnalysis, parallelOpts, async (item, ct) =>
                        {
                            try
                            {
                                var processed = await articleProcessor.ProcessAsync(item, ct);

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

                            // Phase 2: Embedding-based sentiment + topic (pure math, thread-safe)
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

                            analyzeTask.Increment(1);
                        });

                        // Build analyzedItems after parallel completion (preserves order)
                        foreach (var item in needsAnalysis)
                        {
                            analyzedItems.Add((item.Title, item.Summary!, item.DetectedTopic ?? "general",
                                item.SentimentScore, item.Url ?? "", item.RelevanceScore));
                        }
                    }

                    // Save to storage + index into FTS5 for keyword pre-filtering
                    // Batch all writes in a single SQLite transaction for performance
                    var batchEntries = itemsToAnalyze.Select(item =>
                    {
                        var kwProfile = DocumentProfileService.ExtractProfile(item.Title, item.Content ?? "");
                        if (string.IsNullOrEmpty(item.Keywords))
                            item.Keywords = kwProfile.KeywordsText;
                        return (item, kwProfile);
                    }).ToList();

                    await storage.SaveAndIndexBatchAsync(batchEntries);

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
                allEntities = new List<NerEntity>();
                articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
                // Auto-enable entity extraction when GraphScope is Global or Connective (GraphRAG scope detection)
                extractEntities = settings.Entities
                    || (interpreted?.GraphScope is GraphScope.Global or GraphScope.Connective);

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

                    // Persist entities to SQLite for future runs (enriches theme briefing without --entities)
                    if (articleEntityMap.Count > 0)
                    {
                        foreach (var (ci, ents) in articleEntityMap)
                        {
                            var deduped = ents
                                .GroupBy(e => e.Text.ToLowerInvariant())
                                .Select(g => g.OrderByDescending(e => e.Confidence).First())
                                .ToList();

                            var entityIds = new List<string>();
                            foreach (var entity in deduped)
                            {
                                var entityId = KnowledgeGraphService.GenerateEntityId(entity.Text, entity.Type);
                                entityIds.Add(entityId);
                                await storage.UpsertEntityAsync(entityId, entity.Text, entity.Type, entity.Confidence);
                                await storage.UpsertEntityMentionAsync(entityId, ci.Id, entity.Confidence, ci.Title);
                            }

                            // Build co-occurrence edges in SQLite too
                            for (var ei = 0; ei < entityIds.Count; ei++)
                            {
                                for (var ej = ei + 1; ej < entityIds.Count; ej++)
                                {
                                    await storage.UpsertRelationshipAsync(entityIds[ei], entityIds[ej]);
                                }
                            }
                        }
                    }
                }

                // Layer 3: Graph enrichment — discover related documents via shared entities
                // Uses entity_mentions to find docs sharing 2+ entities with top results
                if (extractEntities && uniqueItems.Count >= 3)
                {
                    var topItemIds = uniqueItems
                        .OrderByDescending(i => i.RelevanceScore)
                        .Take(5)
                        .Select(i => i.Id)
                        .ToList();

                    var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                    var relatedIds = await storage.FindRelatedByEntitiesAsync(
                        topItemIds, excludeIds: existingIds.ToList(), limit: 3);

                    if (relatedIds.Count > 0)
                    {
                        var relatedItems = await storage.LoadItemsByIdsAsync(relatedIds);
                        // Assign slightly lower relevance so they appear after scored items
                        var lowestScore = uniqueItems.Count > 0
                            ? uniqueItems.Min(i => i.RelevanceScore)
                            : 0.1;
                        foreach (var item in relatedItems)
                        {
                            if (!existingIds.Contains(item.Id))
                            {
                                var enriched = item with { Source = item.Source + " (via entities)" };
                                enriched.RelevanceScore = lowestScore * 0.9;
                                uniqueItems.Add(enriched);
                                existingIds.Add(item.Id);
                            }
                        }

                        if (relatedItems.Count > 0)
                            fetchTask.Description = $"[cyan]Graph: +{relatedItems.Count} related[/]";
                        if (settings.DebugPipeline && relatedItems.Count > 0)
                            AnsiConsole.MarkupLine($"[grey]Graph enrichment: +{relatedItems.Count} entity-related items[/]");
                    }
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
                template = settings.Template.ToLowerInvariant();
                isBlogTemplate = template is "blog-article" or "blog-timeline"
                    or "blog-newsletter" or "blog-newsletter-html";

                // finalSummary and templateData already declared outside lambda
                templateData = null;

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

                            summaryTask.Description = $"[cyan]Quality: {qualityAdjusted} adjusted[/]";
                            if (settings.DebugPipeline)
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

                        BlogArticleResult blogResult;
                        using (var articleProcessor = new ArticleProcessor())
                        {
                            var generator = new LongFormDocumentGenerator(
                                ollama, articleProcessor);
                            blogResult = await generator.GenerateAsync(
                                analyzedItems, uniqueItems,
                                userQuery ?? "topic overview",
                                vibe, vibePrompt, articleQueryType,
                                templateDef, cancellationToken);
                        }

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
                        // Only apply for research/qa queries (entity lookups), not news/roundups
                        var sentinelIntent = interpreted?.SentinelIntent?.Intent ?? "";
                        var isEntityQuery = sentinelIntent is "research" or "qa";
                        if (isEntityQuery && !string.IsNullOrWhiteSpace(userQuery)
                            && detectedQueryType != QueryType.Roundup)
                        {
                            var topForDisambig = uniqueItems
                                .OrderByDescending(i => i.RelevanceScore)
                                .Take(settings.Limit)
                                .ToList();

                            var disambiguator = new EntityDisambiguationService();
                            var disambiguation = await disambiguator.DisambiguateFastAsync(
                                topForDisambig, userQuery, embedding, storage);

                            // Filter out clusters irrelevant to the query — prevents e.g.
                            // "Artificial Intelligence" clusters appearing in "strawberry prices" queries
                            if (disambiguation.IsAmbiguous && disambiguation.Clusters.Count >= 2 && queryEmbedding != null)
                            {
                                var relevantClusters = disambiguation.Clusters
                                    .Where(c =>
                                    {
                                        if (c.Items.Count == 0) return false;
                                        var topItem = c.Items.OrderByDescending(i => i.RelevanceScore).First();
                                        if (topItem.Embedding == null) return true; // can't filter without embedding
                                        var sim = EmbeddingService.CosineSimilarity(topItem.Embedding, queryEmbedding);
                                        return sim >= 0.35f; // minimum topical relevance to query
                                    })
                                    .ToList();

                                if (relevantClusters.Count >= 2)
                                {
                                    var entityLines = relevantClusters
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

                // Cleanup old data (before Progress ends)
                await storage.CleanupOldDataAsync(config.Storage.RetentionDays);
                if (vectorStore != null)
                    await vectorStore.CleanupAsync(config.Storage.RetentionDays);
            });

        // === Output (outside Progress block — no more progress bar overlap) ===
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
            // Word-wrap content to prevent the panel from stretching to full terminal width
            var maxContentWidth = Math.Min(AnsiConsole.Profile.Width - 6, 94);
            var wrappedMarkup = WordWrapMarkup(renderedMarkup, maxContentWidth);
            AnsiConsole.Write(new Panel(wrappedMarkup)
                .Header(header)
                .Border(BoxBorder.Rounded)
                .Padding(1, 1));

            // Deterministic sources section — shows which documents were used
            RenderSourcesUsed(analyzedItems, uniqueItems, maxContentWidth);

            // Display evidence briefing with named themes (opt-in via --briefing)
            if (settings.Briefing && analyzedItems.Count > 0)
            {
                // Load entity data for enriched theme briefing
                Dictionary<string, List<(string name, string type, double confidence)>>? itemEntities = null;

                if (articleEntityMap.Count > 0)
                {
                    // Use NER entities from this session
                    itemEntities = articleEntityMap.ToDictionary(
                        ae => ae.item.Id,
                        ae => ae.entities.Select(e => (e.Text, e.Type, (double)e.Confidence)).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // Query stored entities from previous runs (knowledge graph)
                    var itemIds = uniqueItems.Select(u => u.Id).ToList();
                    itemEntities = await storage.GetEntitiesForItemsAsync(itemIds);
                }

                var briefing = ExtractThemeBriefing(analyzedItems, uniqueItems, itemEntities);
                if (briefing.Themes.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var briefingParts = new List<string>();

                    // Corpus coverage line
                    var entityNote = briefing.HasGraphEntities
                        ? $", {briefing.GraphEntityCount} graph entities"
                        : "";
                    var coverageNote = $"[dim]Themes inferred from {briefing.TotalEvidenceItems} evidence items across {briefing.SourceCount} sources{entityNote} (coverage: {briefing.CoveragePercent}%).[/]";
                    briefingParts.Add(coverageNote);
                    var methodNote = briefing.HasGraphEntities
                        ? "[dim]Entity-graph enriched; RRF + in-corpus PageRank; diversity decay applied.[/]"
                        : "[dim]Selected by RRF + in-corpus PageRank; diversity decay applied.[/]";
                    briefingParts.Add(methodNote);
                    briefingParts.Add("");

                    // Named themes with evidence counts and snippets
                    foreach (var theme in briefing.Themes)
                    {
                        var color = theme.TopicLabel.ToLowerInvariant() switch
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

                        var eids = theme.EvidenceIds.Count > 0
                            ? " " + string.Join(", ", theme.EvidenceIds.Take(5).Select(id => $"[dim]E{id}[/]"))
                            : "";
                        briefingParts.Add(
                            $"[{color}]■[/] [bold]{Markup.Escape(theme.ThesisName)}[/] [dim]({theme.SegmentCount} segments)[/]{eids}");

                        // Supporting snippets
                        foreach (var (snippet, eid) in theme.Snippets)
                        {
                            var tag = eid.HasValue ? $" [dim][[E{eid.Value}]][/]" : "";
                            var truncSnippet = snippet.Length > 90 ? snippet[..87] + "..." : snippet;
                            briefingParts.Add($"  [italic dim]\"{Markup.Escape(truncSnippet)}\"[/]{tag}");
                        }

                        // Show entities: typed NER entities when available, else key terms
                        if (theme.GraphEntities.Count > 0)
                        {
                            var typedEntities = string.Join(", ", theme.GraphEntities.Take(6).Select(e =>
                            {
                                var typeColor = e.type switch
                                {
                                    "PER" => "green",
                                    "ORG" => "blue",
                                    "LOC" => "yellow",
                                    _ => "grey"
                                };
                                var typeLabel = e.type switch
                                {
                                    "PER" => "person",
                                    "ORG" => "org",
                                    "LOC" => "loc",
                                    _ => "misc"
                                };
                                return $"[{typeColor}]{Markup.Escape(e.name)}[/][dim]:{typeLabel}[/]";
                            }));
                            briefingParts.Add($"  {typedEntities}");
                        }
                        else if (theme.KeyTerms.Count > 0)
                        {
                            var terms = string.Join(", ", theme.KeyTerms.Select(t => Markup.Escape(t)));
                            briefingParts.Add($"  [dim]Terms: {terms}[/]");
                        }

                        briefingParts.Add("");
                    }

                    // Missing themes / outliers
                    if (briefing.MissingTopics.Count > 0)
                    {
                        var missing = string.Join(", ", briefing.MissingTopics.Select(t =>
                            Markup.Escape(char.ToUpper(t[0]) + t[1..])));
                        briefingParts.Add($"[dim]Not strongly represented: {missing}[/]");
                    }

                    var briefingContent = string.Join("\n", briefingParts).TrimEnd('\n');
                    var wrappedBriefing = WordWrapMarkup(briefingContent, maxContentWidth);
                    AnsiConsole.Write(new Panel(wrappedBriefing)
                        .Header("[bold yellow]Evidence Briefing[/]")
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

        // Send email if requested
        if (settings.SendEmail)
        {
            string emailHtml;
            if (templateData != null)
            {
                // Use full template rendering (blog/newsletter paths)
                emailHtml = outputTemplates.Render(templateData, config.Email.Template);
            }
            else
            {
                // Standard synthesis: build templateData from analyzed items
                // Convert markdown summary to HTML for email rendering
                var overviewHtml = Markdig.Markdown.ToHtml(finalSummary ?? "");
                templateData = new DigestData
                {
                    Date = DateTimeOffset.Now,
                    Vibe = vibe,
                    Query = interpreted?.RawPrompt ?? settings.Prompt,
                    Overview = overviewHtml,
                    Items = analyzedItems.Select(a => new DigestItem
                    {
                        Title = a.title,
                        Url = a.url,
                        Summary = a.summary,
                        Topic = a.topic,
                        Sentiment = a.sentiment
                    }).ToList()
                };
                emailHtml = outputTemplates.Render(templateData, config.Email.Template);
            }

            var emailService = new EmailService(config.Email, apiKeys);
            var subject = config.Email.SubjectTemplate
                .Replace("{{DATE}}", DateTime.Now.ToString("MMMM d, yyyy"))
                .Replace("{{QUERY}}", interpreted?.RawPrompt ?? settings.Prompt ?? "");
            await emailService.SendAsync(emailHtml, subject, settings.EmailTo, cancellationToken);
        }

        if (vectorStore != null)
            await vectorStore.DisposeAsync();

        return 0;
    }

}

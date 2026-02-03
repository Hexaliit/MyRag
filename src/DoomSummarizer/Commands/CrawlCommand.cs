using System.ComponentModel;
#if FEATURE_COMPLETE
using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Services.Transcription;
#endif
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
#if FEATURE_COMPLETE
using DoomSummarizer.Sources.YouTube;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
#endif
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Crawl a website to build a searchable knowledge base.
/// Follows same-domain links from a seed URL, extracts content,
/// computes embeddings, and stores for later querying via 'scroll --name [name]'.
///
/// Cache-aware incremental crawling (default):
///   1. HTTP conditional requests: sends If-None-Match / If-Modified-Since headers
///      using stored ETags and Last-Modified dates → server returns 304 Not Modified
///      without transferring the page body (saves bandwidth).
///   2. Content hash fallback: for servers that don't support ETags, compares a SHA256
///      hash of the page content against the cached hash → skips unchanged pages.
///
/// Use --force to re-process all pages regardless of cache state.
/// </summary>
public sealed class CrawlCommand : AsyncCommand<CrawlCommand.Settings>
{
    public sealed class Settings : CommonSettings
    {
        [CommandArgument(0, "<source>")]
        [Description("Seed URL or local file/directory path")]
        public string Url { get; init; } = "";

        [CommandOption("-d|--depth")]
        [Description("Maximum crawl depth from seed URL")]
        [DefaultValue(3)]
        public int Depth { get; init; } = 3;

        [CommandOption("-m|--max-pages")]
        [Description("Maximum pages to crawl")]
        [DefaultValue(200)]
        public int MaxPages { get; init; } = 200;

        [CommandOption("--delay")]
        [Description("Minimum delay between requests in ms (adaptive — increases for slow servers, floor: 200ms)")]
        [DefaultValue(1000)]
        public int DelayMs { get; init; } = 1000;

        [CommandOption("--concurrency")]
        [Description("Maximum concurrent requests (hard cap: 5)")]
        [DefaultValue(3)]
        public int Concurrency { get; init; } = 3;

        [CommandOption("-g|--glob")]
        [Description("URL path filter pattern (e.g., /blog/* or /docs/*). Only pages matching this pattern are indexed.")]
        public string? Glob { get; init; }

        [CommandOption("--no-entities")]
        [Description("Disable NER entity extraction (enabled by default)")]
        public bool NoEntities { get; init; }

        [CommandOption("-f|--force")]
        [Description("Re-process all pages regardless of cache (default: skip unchanged pages)")]
        public bool Force { get; init; }

        [CommandOption("--ask")]
        [Description("Drop to interactive ask mode while crawling in background")]
        public bool Ask { get; init; }

        [CommandOption("-r|--recurse")]
        [Description("Recurse into subdirectories when source is a local path (default: top-level only)")]
        public bool Recurse { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var isUrl = Uri.TryCreate(settings.Url, UriKind.Absolute, out _) &&
                    (settings.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     settings.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var isLocalPath = File.Exists(settings.Url) || Directory.Exists(settings.Url);

        if (!isUrl && !isLocalPath)
        {
            AnsiConsole.MarkupLine("[red]Error: Please provide a valid URL or local file/directory path.[/]");
            return 1;
        }

        // Check for built-in source overlap — warn if this URL is already covered
        if (isUrl)
        {
            var overlap = GetBuiltInSourceForUrl(settings.Url);
            if (overlap != null)
            {
                AnsiConsole.MarkupLine($"[yellow]Reminder:[/] [bold]{Markup.Escape(settings.Url)}[/] is already covered by the built-in [cyan]{overlap}[/] source.");
                AnsiConsole.MarkupLine($"[grey]The built-in source applies rate limiting and politeness. Use:[/] doomsummarizer scroll \"your query\" [grey](auto-routes to {overlap})[/]");
                AnsiConsole.MarkupLine($"[grey]Continuing with crawl — the crawler's adaptive delay will apply.[/]");
                AnsiConsole.WriteLine();
            }
        }

        // Derive KB name from source
        var kbName = settings.Name ?? (isUrl ? CollectionNaming.FromUrl(settings.Url!) : CollectionNaming.Auto(settings.Url!));

        await using var boot = await CommandBootstrap.CreateAsync(cancellationToken);
        if (!settings.NoEntities)
            await boot.InitializeEntityGraphStoreAsync();

        // YouTube URL: extract metadata + subtitles (no web crawl needed)
#if FEATURE_COMPLETE
        if (isUrl && YouTubeExtractor.IsYouTubeUrl(settings.Url))
            return await ExecuteYouTubeAsync(boot, settings, kbName, cancellationToken);
#endif

        // Local path: ingest files directly (fast, no background crawl needed)
        if (isLocalPath)
            return await ExecuteLocalAsync(boot, settings, kbName, cancellationToken);

        // --ask: background crawl + interactive ask loop
        if (settings.Ask)
            return await ExecuteWithAskAsync(boot, settings, kbName, cancellationToken);

        using var httpClient = HttpClientFactory.CreateDefault();

        var crawlConfig = CreateCrawlConfig(settings, kbName);
        var crawler = new WebCrawlerService(httpClient, crawlConfig);

        // Pre-load URL cache for conditional request headers (ETag / Last-Modified)
        Dictionary<string, (string? etag, string? lastModified)> urlCacheLookup = new(StringComparer.OrdinalIgnoreCase);
        if (!settings.Force)
        {
            // Build lookup from all cached URLs — the crawler will use this
            // to send If-None-Match / If-Modified-Since headers
            var allCached = await boot.Storage.GetAllUrlCacheEntriesAsync();
            foreach (var entry in allCached)
            {
                if (!string.IsNullOrEmpty(entry.ETag) || !string.IsNullOrEmpty(entry.LastModified))
                    urlCacheLookup[entry.Url] = (entry.ETag, entry.LastModified);
            }
        }

        AnsiConsole.MarkupLine($"[bold cyan]Crawling:[/] {Markup.Escape(settings.Url)}");
        if (crawlConfig.GitHubRawMode)
            AnsiConsole.MarkupLine($"[grey]GitHub repo detected — raw markdown mode, filter: {Markup.Escape(crawlConfig.PathFilter ?? "*")}[/]");
        else if (crawlConfig.ContentScopedLinks)
            AnsiConsole.MarkupLine($"[grey]GitHub repo detected — scoping to content links, filter: {Markup.Escape(crawlConfig.PathFilter ?? "*")}[/]");
        var filterInfo = !string.IsNullOrEmpty(crawlConfig.PathFilter) && !crawlConfig.ContentScopedLinks
            ? $" | filter: {crawlConfig.PathFilter}" : "";
        var cacheMode = settings.Force ? "[yellow]force[/]" : "[green]incremental[/]";
        var cacheStats = urlCacheLookup.Count > 0 ? $" | {urlCacheLookup.Count} cached ETags" : "";
        AnsiConsole.MarkupLine($"[grey]KB name: {Markup.Escape(kbName)} | depth: {settings.Depth} | max: {settings.MaxPages} pages{filterInfo} | mode: {cacheMode}{cacheStats}[/]");
        AnsiConsole.WriteLine();

        // Track both new/changed items and cached (unchanged) items
        var newItems = new List<Models.ContentItem>();
        // URL-cached items: already in storage, need Lucene indexing for this collection
        var urlCachedItems = new List<Models.ContentItem>();
        // Separate counters for HTTP 304 vs content-hash match
        var httpNotModifiedCount = 0;
        var contentHashCachedCount = 0;

        // Track ETags/Last-Modified captured from responses (URL → headers)
        var capturedHeaders = new Dictionary<string, (string? etag, string? lastModified)>(StringComparer.OrdinalIgnoreCase);

        await ProgressHelper.RunAsync(async ctx =>
            {
                // Stage 1: Crawl, extract, and filter by cache (ETag + content hash)
                var crawlTask = ctx.AddTask("[cyan]Crawling pages[/]", maxValue: settings.MaxPages);

                // Cache provider: returns stored ETags/Last-Modified for conditional requests
                Func<string, (string? etag, string? lastModified)>? cacheProvider =
                    settings.Force ? null : url =>
                    {
                        // Normalize to match the lookup key format
                        var normalized = url.Split('?')[0].Split('#')[0].TrimEnd('/').ToLowerInvariant();
                        return urlCacheLookup.TryGetValue(normalized, out var cached) ? cached : (null, null);
                    };

                await foreach (var result in crawler.CrawlAsync(
                    settings.Url,
                    cacheProvider: cacheProvider,
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
                    // Capture response headers for cache storage
                    if (!string.IsNullOrEmpty(result.Url) && (result.ETag != null || result.LastModified != null))
                        capturedHeaders[result.Url] = (result.ETag, result.LastModified);

                    // HTTP 304 Not Modified — server confirmed no changes (most efficient)
                    if (result.NotModified)
                    {
                        httpNotModifiedCount++;
                        // Bump hit count in URL cache
                        if (!string.IsNullOrEmpty(result.Url))
                            await boot.Storage.UpdateUrlCacheAsync(result.Url, null, result.ETag, result.LastModified, 0);
                        continue;
                    }

                    var item = result.Item;
                    if (item == null) continue;

                    // Content hash fallback: for servers that don't support ETags
                    if (!settings.Force && !string.IsNullOrEmpty(item.Url) && !string.IsNullOrEmpty(item.Content))
                    {
                        var contentHash = ItemProcessor.ComputeContentHash(item.Content);
                        if (await boot.Storage.IsContentUnchangedAsync(item.Url, contentHash))
                        {
                            contentHashCachedCount++;
                            // Update cache: bump hit count + store any new ETags from this response
                            var (etag, lastMod) = capturedHeaders.TryGetValue(item.Url, out var h) ? h : (null, null);
                            await boot.Storage.UpdateUrlCacheAsync(item.Url, contentHash, etag, lastMod, item.Content.Length);
                            continue;
                        }
                    }

                    // URL cache check: if this URL already exists in storage (from any source),
                    // web-validate it and collect for Lucene backfill instead of re-indexing
                    if (!settings.Force && !string.IsNullOrEmpty(item.Url))
                    {
                        var existing = await boot.Storage.FindByUrlAsync(item.Url);
                        if (existing != null)
                        {
                            await boot.Storage.WebValidateByUrlAsync(item.Url);
                            urlCachedItems.Add(existing.ToContentItem());
                            continue;
                        }
                    }

                    newItems.Add(item);
                }

                var totalCached = httpNotModifiedCount + contentHashCachedCount;
                crawlTask.Value = settings.MaxPages;
                crawlTask.Description = settings.Force
                    ? $"[green]Crawled {crawler.PagesVisited} pages, extracted {crawler.PagesExtracted}, skipped {crawler.PagesSkipped}[/]"
                    : $"[green]Crawled {crawler.PagesVisited} pages, {newItems.Count} new/changed, {totalCached} cached ({httpNotModifiedCount} HTTP 304, {contentHashCachedCount} hash), {crawler.PagesSkipped} skipped[/]";

                if (newItems.Count == 0)
                {
                    crawlTask.Description = $"[green]All {totalCached} pages unchanged since last crawl[/]";
                    return;
                }

                // Stage 2: Compute embeddings for new/changed items
                var embedTask = ctx.AddTask("[cyan]Computing embeddings[/]", maxValue: newItems.Count);

                foreach (var item in newItems)
                {
                    var textToEmbed = ItemProcessor.PrepareEmbeddingText(item.Title, item.Content);
                    item.Embedding = await boot.Embedding.EmbedAsync(textToEmbed, cancellationToken);
                    embedTask.Increment(1);
                }

                embedTask.Description = $"[green]Embedded {newItems.Count} pages[/]";

                using var processor = await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, boot.EntityStore, collectionName: kbName, ct: cancellationToken);

                // Stage 3: NER entity extraction (optional)
                // Entity persistence is deferred to after Stage 4 (item save) to avoid FK violations
                var articleEntityMap = new List<(Models.ContentItem item, List<NerEntity> entities)>();
                if (!settings.NoEntities)
                {
                    var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: newItems.Count);
                    using var nerService = new NerService();
                    if (nerService.IsAvailable)
                    {
                        await nerService.InitializeAsync();
                        foreach (var item in newItems)
                        {
                            var textForNer = ItemProcessor.PrepareNerText(item.Title, item.Content);
                            var entities = await nerService.ExtractEntitiesAsync(textForNer);
                            if (entities.Count > 0)
                            {
                                var entityText = string.Join(", ", entities
                                    .OrderByDescending(e => e.Confidence)
                                    .Take(10)
                                    .Select(e => $"{e.Text} ({e.Type})"));
                                item.Summary = (item.Summary ?? "") + $" [Entities: {entityText}]";

                                articleEntityMap.Add((item, entities));
                            }
                            nerTask.Increment(1);
                        }
                    }
                    nerTask.Description = $"[green]Entities extracted from {newItems.Count} pages[/]";
                }

                // Stage 4: Store in knowledge base + update URL cache with ETags
                var storeTask = ctx.AddTask("[cyan]Saving to knowledge base[/]", maxValue: newItems.Count);

                // Add topic and sentiment from embeddings
                foreach (var item in newItems)
                {
                    processor.ScoreSentimentAndTopic(item);

                    item.Summary ??= item.Content?.Length > 300
                        ? item.Content[..300] + "..."
                        : item.Content ?? item.Title;

                    // Compute keyword profile, save item, and index into FTS5
                    await processor.IndexItemAsync(item);

                    // Update URL cache: content hash + any ETags/Last-Modified from the response
                    if (!string.IsNullOrEmpty(item.Url) && !string.IsNullOrEmpty(item.Content))
                    {
                        var contentHash = ItemProcessor.ComputeContentHash(item.Content);
                        var (etag, lastMod) = capturedHeaders.TryGetValue(item.Url, out var h) ? h : (null, null);
                        await boot.Storage.UpdateUrlCacheAsync(item.Url, contentHash, etag, lastMod, item.Content.Length);
                    }

                    storeTask.Increment(1);
                }

                storeTask.Description = $"[green]Saved {newItems.Count} pages to KB '{kbName}'[/]";

                // Compensate: URL-cached items are already in SQLite but need to be in
                // this collection's Lucene index. Also backfill from storage for the
                // current source to keep the index populated.
                if (urlCachedItems.Count > 0)
                {
                    processor.EnsureInLucene(urlCachedItems);

                    // Re-search storage for this collection to fill remaining Lucene gaps
                    var sourceTag = $"crawl:{kbName}";
                    var shortfall = Math.Min(urlCachedItems.Count, settings.MaxPages - newItems.Count);
                    if (shortfall > 0)
                    {
                        var storedItems = await boot.Storage.GetItemsBySourceAsync(sourceTag, limit: shortfall);
                        processor.EnsureInLucene(storedItems.Select(s => s.ToContentItem()));
                    }

                    storeTask.Description = $"[green]Saved {newItems.Count} pages + {urlCachedItems.Count} cached to KB '{kbName}'[/]";
                }

                // Stage 5: Persist entities (deferred from Stage 3 — items must exist in DB first)
                if (articleEntityMap.Count > 0)
                {
                    foreach (var (item, entities) in articleEntityMap)
                    {
                        await processor.PersistEntitiesAsync(item, entities);
                    }
                }
            });

        // Summary
        AnsiConsole.WriteLine();

        var allProcessed = newItems;
        var totalCachedFinal = httpNotModifiedCount + contentHashCachedCount;
        var topicDist = allProcessed
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
        table.AddRow("New/changed", $"{newItems.Count}");
        if (urlCachedItems.Count > 0)
            table.AddRow("URL cache (web-validated)", $"{urlCachedItems.Count}");
        if (httpNotModifiedCount > 0)
            table.AddRow("HTTP 304 (not modified)", $"{httpNotModifiedCount}");
        if (contentHashCachedCount > 0)
            table.AddRow("Content hash match", $"{contentHashCachedCount}");
        table.AddRow("Total cached", $"{totalCachedFinal + urlCachedItems.Count}");
        table.AddRow("Skipped", $"{crawler.PagesSkipped}");
        if (crawler.RetryCount > 0)
            table.AddRow("Rate-limit retries", $"{crawler.RetryCount}");
        table.AddRow("Final adaptive delay", $"{crawler.AdaptiveDelayMs}ms");
        table.AddRow("With embeddings", $"{newItems.Count(i => i.Embedding != null)}");
        if (newItems.Count > 0)
        {
            table.AddRow("Topics", string.Join(", ", topicDist.Select(g => $"{g.Key} ({g.Count()})")));
            table.AddRow("Avg quality", newItems.Where(i => i.ContentStructure != null)
                .Select(i => i.ContentStructure!.QualityScore)
                .DefaultIfEmpty(0)
                .Average()
                .ToString("F2"));
        }

        AnsiConsole.Write(table);

        if ((totalCachedFinal > 0 || urlCachedItems.Count > 0) && newItems.Count == 0)
        {
            AnsiConsole.MarkupLine($"\n[grey]All pages unchanged. Use --force to re-process anyway.[/]");
        }

        AnsiConsole.MarkupLine($"\n[grey]Query this KB with:[/] doomsummarizer scroll \"your question\" --name {Markup.Escape(kbName)}");
        AnsiConsole.MarkupLine($"[grey]Browse contents:[/] doomsummarizer show {Markup.Escape(kbName)}");

        return 0;
    }

    private async Task<int> ExecuteLocalAsync(
        CommandBootstrap boot, Settings settings, string kbName, CancellationToken ct)
    {
        // Resolve files using ScrollCommand's shared helper
        var (files, collectionName, _) = ScrollCommand.ResolveLocalSources(
            new[] { settings.Url }, settings.Name, settings.Recurse);

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported files found in the specified path.[/]");
            return 1;
        }

        // Use the resolved collection name (or the one we already derived)
        if (!string.IsNullOrEmpty(collectionName))
            kbName = collectionName;

        AnsiConsole.MarkupLine($"[bold cyan]Ingesting:[/] {Markup.Escape(settings.Url)}");
        AnsiConsole.MarkupLine($"[grey]KB name: {Markup.Escape(kbName)} | {files.Count} file(s)[/]");
        AnsiConsole.WriteLine();

        // Ingest via shared pipeline
        string sourceFilter;
        int itemCount;
        IngestDocumentType docType;

        await ProgressHelper.RunAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[cyan]Ingesting files[/]", maxValue: 100);
                (sourceFilter, itemCount, docType) = await ScrollCommand.IngestLocalFilesAsync(
                    files, kbName, boot, progressTask, settings.Force, ct);
            });

        // If --ask: enter interactive ask loop against the newly-ingested collection
        if (settings.Ask)
        {
            return await boot.StartAskLoopAsync(new InteractiveAskOptions(
                Source: $"file:{kbName}",
                Name: kbName,
                Days: 0,
                TopK: 10,
                Once: false,
                Quiet: settings.Quiet,
                InitialQuestion: null), ct);
        }

        // Summary
        AnsiConsole.MarkupLine($"\n[grey]Query this KB with:[/] doomsummarizer scroll \"your question\" --name {Markup.Escape(kbName)}");
        AnsiConsole.MarkupLine($"[grey]Ask interactively:[/] doomsummarizer crawl {Markup.Escape(settings.Url)} --ask");
        AnsiConsole.MarkupLine($"[grey]Browse contents:[/] doomsummarizer show {Markup.Escape(kbName)}");

        return 0;
    }

#if FEATURE_COMPLETE
    private async Task<int> ExecuteYouTubeAsync(
        CommandBootstrap boot, Settings settings, string kbName, CancellationToken ct)
    {
        var extractor = new YouTubeExtractor();

        AnsiConsole.MarkupLine($"[bold cyan]YouTube:[/] {Markup.Escape(settings.Url)}");

        // Try to create a Whisper transcription delegate for audio fallback
        var audioTranscriber = await CreateAudioTranscriberAsync(ct);
        if (audioTranscriber != null)
            AnsiConsole.MarkupLine("[grey]Audio transcription available (Whisper) — will use if no captions[/]");

        // Extract metadata + subtitles (with optional audio fallback)
        YouTubeExtractionResult? result = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Extracting YouTube video...", async ctx =>
            {
                result = await extractor.ExtractAsync(
                    settings.Url,
                    msg => ctx.Status = $"[cyan]{Markup.Escape(msg)}[/]",
                    ct,
                    audioTranscriber);
            });

        if (result == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to extract YouTube video.[/]");
            return 1;
        }

        // Use video-ID-based KB name if not explicitly set
        if (settings.Name == null)
            kbName = $"yt-{result.VideoId}";

        AnsiConsole.MarkupLine($"[grey]Title: {Markup.Escape(result.Title)}[/]");
        AnsiConsole.MarkupLine($"[grey]Channel: {Markup.Escape(result.Channel)} | Duration: {result.Duration}[/]");
        AnsiConsole.MarkupLine($"[grey]Extracted {result.Items.Count} content chunks[/]");

        if (result.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No subtitles/captions available for this video.[/]");
            return 0;
        }

        // Embed and index all items
        await ProgressHelper.RunAsync(async ctx =>
            {
                var embedTask = ctx.AddTask("[cyan]Computing embeddings[/]", maxValue: result.Items.Count);
                using var processor = await ItemProcessor.CreateAsync(
                    boot.Embedding, boot.Storage, boot.EntityStore, collectionName: kbName, ct: ct);

                var texts = result.Items
                    .Select(i => ItemProcessor.PrepareEmbeddingText(i.Title, i.Content))
                    .ToList();

                var embeddings = await boot.Embedding.EmbedBatchAsync(texts, ct);
                embedTask.Value = result.Items.Count;
                embedTask.Description = $"[green]Embedded {result.Items.Count} chunks[/]";

                var indexTask = ctx.AddTask("[cyan]Indexing content[/]", maxValue: result.Items.Count);
                for (var i = 0; i < result.Items.Count; i++)
                {
                    var item = result.Items[i];
                    if (i < embeddings.Length)
                        item.Embedding = embeddings[i];
                    processor.ScoreSentimentAndTopic(item);
                }

                await processor.IndexBatchAsync(result.Items);
                indexTask.Value = result.Items.Count;
                indexTask.Description = $"[green]Indexed {result.Items.Count} chunks to KB '{kbName}'[/]";
            });

        // If --ask: enter interactive ask loop
        if (settings.Ask)
        {
            return await boot.StartAskLoopAsync(new InteractiveAskOptions(
                Source: $"youtube:{kbName}",
                Name: kbName,
                Days: 0,
                TopK: 10,
                Once: false,
                Quiet: settings.Quiet,
                InitialQuestion: null), ct);
        }

        // Summary
        AnsiConsole.MarkupLine($"\n[grey]Query this KB with:[/] doomsummarizer scroll \"your question\" --name {Markup.Escape(kbName)}");
        AnsiConsole.MarkupLine($"[grey]Ask interactively:[/] doomsummarizer crawl {Markup.Escape(settings.Url)} --ask");
        AnsiConsole.MarkupLine($"[grey]Browse contents:[/] doomsummarizer show {Markup.Escape(kbName)}");

        return 0;
    }
#endif

    private async Task<int> ExecuteWithAskAsync(
        CommandBootstrap boot, Settings settings, string kbName, CancellationToken ct)
    {
        // Initialize LLM stack for ask mode
        var ollama = boot.CreateOllama();
        var llmRouter = await boot.InitializeLlmStackAsync(ct: ct);

        await boot.TryInitializeEntityGraphStoreAsync();

        var ollamaAvailable = await ollama.IsAvailableAsync();
        var hasCloudLlm = llmRouter.HasCloudProvider;
        if (!ollamaAvailable && !hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[yellow]No LLM available (Ollama down, no cloud keys).[/] Answers will be limited to evidence listing.");
            AnsiConsole.MarkupLine("[grey]Start Ollama: ollama serve  —or—  set OPENAI_API_KEY / ANTHROPIC_API_KEY[/]");
        }
        else if (!ollamaAvailable && hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[cyan]Ollama not available — using cloud LLM provider[/]");
        }

        AnsiConsole.MarkupLine($"[bold cyan]Crawling:[/] {Markup.Escape(settings.Url)}");
        AnsiConsole.MarkupLine($"[grey]KB name: {Markup.Escape(kbName)} | depth: {settings.Depth} | max: {settings.MaxPages} pages | background mode[/]");
        AnsiConsole.WriteLine();

        // Start background crawl
        await using var crawlSession = BackgroundCrawlSession.Start(boot, settings, kbName, ct);

        // Start interactive ask loop with crawl progress channel
        var askOptions = new InteractiveAskOptions(
            Source: $"crawl:{kbName}",
            Name: kbName,
            Days: 0,
            TopK: 10,
            Once: false,
            Quiet: settings.Quiet,
            InitialQuestion: null,
            CrawlProgress: crawlSession.Progress,
            IsCrawlRunning: () => crawlSession.IsRunning);

        var loop = new InteractiveAskLoop(boot, ollama, llmRouter, ollamaAvailable, askOptions);
        var result = await loop.RunAsync(ct);

        // User exited ask loop — stop crawl if still running
        if (crawlSession.IsRunning)
        {
            AnsiConsole.MarkupLine("[grey]Stopping background crawl...[/]");
            crawlSession.RequestStop();
        }

        // Wait for crawl to finish and display summary
        try
        {
            var crawlResult = await crawlSession.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            DisplayCrawlSummary(crawlResult);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[grey]Crawl stopped.[/]");
        }
        catch (TimeoutException)
        {
            AnsiConsole.MarkupLine("[grey]Crawl shutdown timed out.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Crawl ended with error: {Markup.Escape(ex.Message)}[/]");
        }

        return result;
    }

    private static void DisplayCrawlSummary(CrawlSessionResult r)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Title($"[bold cyan]Crawl Summary: {Markup.Escape(r.KbName)}[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Pages crawled", $"{r.PagesVisited}");
        table.AddRow("Pages extracted", $"{r.PagesExtracted}");
        table.AddRow("New/changed", $"{r.NewChanged}");
        if (r.HttpNotModified > 0)
            table.AddRow("HTTP 304 (not modified)", $"{r.HttpNotModified}");
        if (r.ContentHashCached > 0)
            table.AddRow("Content hash match", $"{r.ContentHashCached}");
        table.AddRow("Skipped", $"{r.PagesSkipped}");
        if (r.RetryCount > 0)
            table.AddRow("Rate-limit retries", $"{r.RetryCount}");
        table.AddRow("Final adaptive delay", $"{r.FinalAdaptiveDelayMs}ms");

        AnsiConsole.Write(table);
    }

#if FEATURE_COMPLETE
    /// <summary>
    /// Try to create a Whisper-based audio transcription delegate.
    /// Returns null if Whisper is not available (model not downloaded, etc.).
    /// </summary>
    private static async Task<AudioTranscriberDelegate?> CreateAudioTranscriberAsync(CancellationToken ct)
    {
        try
        {
            var config = Options.Create(new AudioConfig());
            var downloader = new WhisperModelDownloader(NullLogger<WhisperModelDownloader>.Instance);
            var whisper = new WhisperTranscriptionService(
                config,
                NullLogger<WhisperTranscriptionService>.Instance,
                downloader);

            if (!await whisper.IsAvailableAsync(ct))
                return null;

            return async (audioPath, token) =>
            {
                var transcript = await whisper.TranscribeAsync(audioPath, cancellationToken: token);
                return transcript.Segments
                    .Select(s => new TranscriptSegmentInfo(s.Start, s.End, s.Text, s.Confidence ?? 0))
                    .ToList();
            };
        }
        catch
        {
            return null;
        }
    }
#endif

    /// <summary>
    /// Create a CrawlConfig from command settings with auto-detection for GitHub repos.
    /// When a GitHub repo URL is detected:
    ///   - PathFilter is auto-set to /{owner}/{repo}/** (unless --glob is explicit)
    ///   - ContentScopedLinks is enabled (only follow links from the article content,
    ///     not GitHub UI navigation links)
    /// Used by both the foreground crawl path and BackgroundCrawlSession.
    /// </summary>
    internal static CrawlConfig CreateCrawlConfig(Settings settings, string kbName)
    {
        var config = new CrawlConfig
        {
            Name = kbName,
            MaxDepth = settings.Depth,
            MaxPages = settings.MaxPages,
            DelayMs = settings.DelayMs,
            MaxConcurrency = settings.Concurrency,
            TimeoutSeconds = 15,
            PathFilter = settings.Glob
        };

        if (TryGetGitHubRepoScope(settings.Url, out var repoScope))
        {
            config = config with
            {
                PathFilter = settings.Glob ?? $"{repoScope}/**",
                ContentScopedLinks = true,
                GitHubRawMode = true
            };
        }

        return config;
    }

    /// <summary>
    /// Detect GitHub repo URLs and extract the /{owner}/{repo} path prefix for scoping.
    /// Matches: github.com/{owner}/{repo}/blob/..., github.com/{owner}/{repo}/tree/..., etc.
    /// </summary>
    internal static bool TryGetGitHubRepoScope(string url, out string repoPathPrefix)
    {
        repoPathPrefix = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2) return false;

        repoPathPrefix = $"/{segments[0]}/{segments[1]}";
        return true;
    }

    /// <summary>
    /// Check if a URL's domain matches a known built-in source.
    /// Returns the source name if matched, null otherwise.
    /// </summary>
    private static string? GetBuiltInSourceForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();

        // Domain → built-in source name
        return host switch
        {
            _ when host.Contains("news.ycombinator.com") || host.Contains("hacker-news") => "hn",
            _ when host.Contains("reddit.com") || host.Contains("old.reddit.com") => "reddit",
            _ when host.Contains("bbc.co.uk") || host.Contains("bbc.com") => "bbc",
            _ when host.Contains("theguardian.com") => "guardian",
            _ when host.Contains("reuters.com") => "reuters",
            _ when host.Contains("cnn.com") => "cnn",
            _ when host.Contains("arstechnica.com") => "ars",
            _ when host.Contains("theverge.com") => "verge",
            _ when host.Contains("stackoverflow.com") => "so",
            _ when host.Contains("lobste.rs") => "lobsters",
            _ when host.Contains("dev.to") => "devto",
            _ when host.Contains("techcrunch.com") => "techcrunch",
            _ when host.Contains("wired.com") => "wired",
            _ when host.Contains("theregister.com") => "theregister",
            _ when host.Contains("npr.org") => "npr",
            _ when host.Contains("sciencedaily.com") => "sciencedaily",
            _ when host.Contains("arxiv.org") => "arxiv",
            _ when host.Contains("wikipedia.org") => "wikipedia",
            _ => null
        };
    }
}

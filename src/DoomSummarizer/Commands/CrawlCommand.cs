using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using DoomSummarizer.Services;
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

        [CommandOption("--entities")]
        [Description("Enable NER entity extraction and persist to knowledge graph")]
        public bool Entities { get; init; }

        [CommandOption("-f|--force")]
        [Description("Re-process all pages regardless of cache (default: skip unchanged pages)")]
        public bool Force { get; init; }

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

        // Pre-load URL cache for conditional request headers (ETag / Last-Modified)
        Dictionary<string, (string? etag, string? lastModified)> urlCacheLookup = new(StringComparer.OrdinalIgnoreCase);
        if (!settings.Force)
        {
            // Build lookup from all cached URLs — the crawler will use this
            // to send If-None-Match / If-Modified-Since headers
            var allCached = await storage.GetAllUrlCacheEntriesAsync();
            foreach (var entry in allCached)
            {
                if (!string.IsNullOrEmpty(entry.ETag) || !string.IsNullOrEmpty(entry.LastModified))
                    urlCacheLookup[entry.Url] = (entry.ETag, entry.LastModified);
            }
        }

        AnsiConsole.MarkupLine($"[bold cyan]Crawling:[/] {Markup.Escape(settings.Url)}");
        var filterInfo = !string.IsNullOrEmpty(settings.Glob) ? $" | filter: {settings.Glob}" : "";
        var cacheMode = settings.Force ? "[yellow]force[/]" : "[green]incremental[/]";
        var cacheStats = urlCacheLookup.Count > 0 ? $" | {urlCacheLookup.Count} cached ETags" : "";
        AnsiConsole.MarkupLine($"[grey]KB name: {Markup.Escape(kbName)} | depth: {settings.Depth} | max: {settings.MaxPages} pages{filterInfo} | mode: {cacheMode}{cacheStats}[/]");
        AnsiConsole.WriteLine();

        // Track both new/changed items and cached (unchanged) items
        var newItems = new List<Models.ContentItem>();
        // Separate counters for HTTP 304 vs content-hash match
        var httpNotModifiedCount = 0;
        var contentHashCachedCount = 0;

        // Track ETags/Last-Modified captured from responses (URL → headers)
        var capturedHeaders = new Dictionary<string, (string? etag, string? lastModified)>(StringComparer.OrdinalIgnoreCase);

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
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
                            await storage.UpdateUrlCacheAsync(result.Url, null, result.ETag, result.LastModified, 0);
                        continue;
                    }

                    var item = result.Item;
                    if (item == null) continue;

                    // Content hash fallback: for servers that don't support ETags
                    if (!settings.Force && !string.IsNullOrEmpty(item.Url) && !string.IsNullOrEmpty(item.Content))
                    {
                        var contentHash = ComputeContentHash(item.Content);
                        if (await storage.IsContentUnchangedAsync(item.Url, contentHash))
                        {
                            contentHashCachedCount++;
                            // Update cache: bump hit count + store any new ETags from this response
                            var (etag, lastMod) = capturedHeaders.TryGetValue(item.Url, out var h) ? h : (null, null);
                            await storage.UpdateUrlCacheAsync(item.Url, contentHash, etag, lastMod, item.Content.Length);
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
                    var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                    if (textToEmbed.Length > 1000)
                        textToEmbed = textToEmbed[..1000];
                    item.Embedding = embedding.Embed(textToEmbed);
                    embedTask.Increment(1);
                }

                embedTask.Description = $"[green]Embedded {newItems.Count} pages[/]";

                var processor = new ItemProcessor(embedding, storage);

                // Stage 3: NER entity extraction (optional)
                // Entity persistence is deferred to after Stage 4 (item save) to avoid FK violations
                var articleEntityMap = new List<(Models.ContentItem item, List<NerEntity> entities)>();
                if (settings.Entities)
                {
                    var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: newItems.Count);
                    using var nerService = new NerService();
                    if (nerService.IsAvailable)
                    {
                        await nerService.InitializeAsync();
                        foreach (var item in newItems)
                        {
                            var textForNer = $"{item.Title} {item.Content?[..Math.Min(item.Content.Length, 1000)] ?? ""}";
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
                        var contentHash = ComputeContentHash(item.Content);
                        var (etag, lastMod) = capturedHeaders.TryGetValue(item.Url, out var h) ? h : (null, null);
                        await storage.UpdateUrlCacheAsync(item.Url, contentHash, etag, lastMod, item.Content.Length);
                    }

                    storeTask.Increment(1);
                }

                storeTask.Description = $"[green]Saved {newItems.Count} pages to KB '{kbName}'[/]";

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
        if (httpNotModifiedCount > 0)
            table.AddRow("HTTP 304 (not modified)", $"{httpNotModifiedCount}");
        if (contentHashCachedCount > 0)
            table.AddRow("Content hash match", $"{contentHashCachedCount}");
        table.AddRow("Total cached", $"{totalCachedFinal}");
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

        if (totalCachedFinal > 0 && newItems.Count == 0)
        {
            AnsiConsole.MarkupLine($"\n[grey]All pages unchanged. Use --force to re-process anyway.[/]");
        }

        AnsiConsole.MarkupLine($"\n[grey]Query this KB with:[/] doomsummarizer scroll \"your question\" --name {Markup.Escape(kbName)}");
        AnsiConsole.MarkupLine($"[grey]Browse contents:[/] doomsummarizer show {Markup.Escape(kbName)}");

        return 0;
    }

    /// <summary>
    /// Compute a SHA256 content hash for cache comparison.
    /// </summary>
    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

using System.Reflection;
using DoomSummarizer.Commands;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
/// Crawls documentation starting from a seed URL (typically the project README),
/// discovers linked docs, and indexes via the same pipeline as CrawlCommand.
/// No hardcoded document list — the crawler follows markdown links.
///
/// Same ingestion steps as all other paths:
///   Crawl → Embed → Score → Keywords → SQLite → FTS5 → Lucene → NER → Entity graph
/// </summary>
public sealed class ManualLoader
{
    public const string ManualSource = "manual";

    private readonly StorageService _storage;
    private readonly IEmbeddingService _embedding;

    public ManualLoader(StorageService storage, IEmbeddingService embedding)
    {
        _storage = storage;
        _embedding = embedding;
    }

    /// <summary>
    /// Check whether the manual corpus has already been indexed.
    /// </summary>
    public async Task<bool> IsManualLoadedAsync()
    {
        var items = await _storage.GetRecentItemsAsync(days: 36500, source: ManualSource);
        return items.Count > 0;
    }

    /// <summary>
    /// Crawl documentation from seed URL, then embed, score, index, and extract entities.
    /// Uses the same WebCrawlerService + ItemProcessor pipeline as CrawlCommand.
    /// </summary>
    public async Task LoadManualAsync(ItemProcessor processor, bool refresh, CancellationToken ct)
    {
        var manifest = LoadManifest();

        if (refresh)
        {
            AnsiConsole.MarkupLine("[grey]Clearing existing manual corpus...[/]");
            await _storage.DeleteItemsBySourceAsync(ManualSource);
        }

        AnsiConsole.MarkupLine($"[cyan]Loading manual: {manifest.Name} v{manifest.Version}[/]");
        AnsiConsole.MarkupLine($"[grey]Seed: {Markup.Escape(manifest.SeedUrl)}[/]");

        // Build crawl config — same logic as CrawlCommand.CreateCrawlConfig
        var crawlConfig = new CrawlConfig
        {
            Name = "manual",
            MaxDepth = manifest.MaxDepth,
            MaxPages = manifest.MaxPages,
            DelayMs = 300,
            MaxConcurrency = 2,
            TimeoutSeconds = 15,
            PathFilter = manifest.PathFilter,
            ContentScopedLinks = true,
            GitHubRawMode = true
        };

        // Auto-detect GitHub scope for path filtering
        if (CrawlCommand.TryGetGitHubRepoScope(manifest.SeedUrl, out var repoScope))
        {
            crawlConfig = crawlConfig with
            {
                PathFilter = manifest.PathFilter ?? $"{repoScope}/**",
                GitHubRawMode = true
            };
        }

        // Crawl docs from seed URL
        using var httpClient = HttpClientFactory.CreateDefault();
        var crawler = new WebCrawlerService(httpClient, crawlConfig);
        var newItems = new List<ContentItem>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Stage 1: Crawl
                var crawlTask = ctx.AddTask("[cyan]Crawling documentation[/]", maxValue: manifest.MaxPages);

                await foreach (var result in crawler.CrawlAsync(manifest.SeedUrl, ct: ct))
                {
                    if (result.NotModified || result.Item == null) continue;

                    // Re-tag as manual source (ContentItem has init-only Id/Source)
                    var item = result.Item with
                    {
                        Source = ManualSource,
                        Id = $"manual:{result.Item.Id}",
                        IsEnriched = true
                    };

                    newItems.Add(item);
                    crawlTask.Increment(1);
                    crawlTask.Description = $"[cyan]Crawled {newItems.Count}: {Markup.Escape(item.Title?.Length > 40 ? item.Title[..37] + "..." : item.Title ?? "")}[/]";
                }

                crawlTask.Value = crawlTask.MaxValue;
                crawlTask.Description = $"[green]Crawled {newItems.Count} documentation pages[/]";

                if (newItems.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No documentation pages found. Check the seed URL.[/]");
                    return;
                }

                // Stage 2: Embed
                var embedTask = ctx.AddTask("[cyan]Computing embeddings[/]", maxValue: newItems.Count);
                foreach (var item in newItems)
                {
                    var textToEmbed = ItemProcessor.PrepareEmbeddingText(item.Title, item.Content);
                    item.Embedding = await _embedding.EmbedAsync(textToEmbed, ct);
                    embedTask.Increment(1);
                }
                embedTask.Description = $"[green]Embedded {newItems.Count} pages[/]";

                // Stage 3: NER entity extraction
                var entityMap = new List<(ContentItem item, List<NerEntity> entities)>();
                var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: newItems.Count);

                NerService? nerService = null;
                try
                {
                    nerService = new NerService();
                    if (!nerService.IsAvailable)
                    {
                        nerService.Dispose();
                        nerService = null;
                    }
                    else
                    {
                        await nerService.InitializeAsync();
                    }
                }
                catch
                {
                    nerService?.Dispose();
                    nerService = null;
                }

                if (nerService != null)
                {
                    try
                    {
                        foreach (var item in newItems)
                        {
                            var textForNer = ItemProcessor.PrepareNerText(item.Title, item.Content);
                            var entities = await nerService.ExtractEntitiesAsync(textForNer);
                            if (entities.Count > 0)
                                entityMap.Add((item, entities));
                            nerTask.Increment(1);
                        }
                    }
                    finally
                    {
                        nerService.Dispose();
                    }
                }
                nerTask.Value = nerTask.MaxValue;
                nerTask.Description = nerService != null
                    ? $"[green]Extracted entities from {newItems.Count} pages[/]"
                    : "[grey]NER model not available — skipped[/]";

                // Stage 4: Score + Index (keywords → SQLite → FTS5 → keyword corpus → Lucene)
                var indexTask = ctx.AddTask("[cyan]Indexing[/]", maxValue: newItems.Count);
                foreach (var item in newItems)
                {
                    processor.ScoreSentimentAndTopic(item);

                    item.Summary ??= item.Content?.Length > 300
                        ? item.Content[..300] + "..."
                        : item.Content ?? item.Title;

                    await processor.IndexItemAsync(item);
                    indexTask.Increment(1);
                }
                indexTask.Description = $"[green]Indexed {newItems.Count} pages[/]";

                // Stage 5: Persist entities (deferred — items must exist in DB first for FK)
                if (entityMap.Count > 0)
                {
                    foreach (var (item, entities) in entityMap)
                        await processor.PersistEntitiesAsync(item, entities);
                }

                processor.CommitLucene();
            });

        AnsiConsole.MarkupLine($"[cyan]Manual loaded: {newItems.Count} documentation pages indexed[/]");
    }

    /// <summary>
    /// Load the manifest from the embedded resource.
    /// </summary>
    internal static ManualManifest LoadManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "DoomSummarizer.Resources.manual.manifest.yaml";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<ManualManifest>(reader);
    }
}

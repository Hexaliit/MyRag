using System.Threading.Channels;
using DoomSummarizer.Commands;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Services;

/// <summary>
/// Encapsulates the 5-stage crawl pipeline (crawl, embed, NER, save, persist entities)
/// running on a background Task. Reports progress via a bounded Channel and exposes
/// an atomic ItemsIndexedSoFar counter for the ask loop to observe.
/// </summary>
public sealed class BackgroundCrawlSession : IAsyncDisposable
{
    private readonly Channel<CrawlProgressUpdate> _channel;
    private readonly CancellationTokenSource _internalCts;
    private readonly CancellationTokenSource _linkedCts;
    private readonly int[] _sharedCounter; // [0] = items indexed, shared with background task

    public ChannelReader<CrawlProgressUpdate> Progress => _channel.Reader;
    public Task<CrawlSessionResult> Completion { get; }
    public bool IsRunning => !Completion.IsCompleted;
    public int ItemsIndexedSoFar => Volatile.Read(ref _sharedCounter[0]);

    private BackgroundCrawlSession(
        Channel<CrawlProgressUpdate> channel,
        CancellationTokenSource internalCts,
        CancellationTokenSource linkedCts,
        int[] sharedCounter,
        Task<CrawlSessionResult> completion)
    {
        _channel = channel;
        _internalCts = internalCts;
        _linkedCts = linkedCts;
        _sharedCounter = sharedCounter;
        Completion = completion;
    }

    public void RequestStop() => _internalCts.Cancel();

    public static BackgroundCrawlSession Start(
        CommandBootstrap boot,
        CrawlCommand.Settings settings,
        string kbName,
        CancellationToken commandCt)
    {
        var channel = Channel.CreateBounded<CrawlProgressUpdate>(
            new BoundedChannelOptions(50)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true
            });

        var internalCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(commandCt, internalCts.Token);
        var sharedCounter = new int[1];

        var completion = Task.Run(
            () => RunPipelineAsync(boot, settings, kbName, channel.Writer, sharedCounter, linkedCts.Token),
            linkedCts.Token);

        return new BackgroundCrawlSession(channel, internalCts, linkedCts, sharedCounter, completion);
    }

    private static async Task<CrawlSessionResult> RunPipelineAsync(
        CommandBootstrap boot,
        CrawlCommand.Settings settings,
        string kbName,
        ChannelWriter<CrawlProgressUpdate> writer,
        int[] sharedCounter,
        CancellationToken ct)
    {

        try
        {
            using var httpClient = HttpClientFactory.CreateDefault();
            var crawlConfig = CrawlCommand.CreateCrawlConfig(settings, kbName);
            var crawler = new WebCrawlerService(httpClient, crawlConfig);

            // Pre-load URL cache for conditional request headers
            var urlCacheLookup = new Dictionary<string, (string? etag, string? lastModified)>(StringComparer.OrdinalIgnoreCase);
            if (!settings.Force)
            {
                var allCached = await boot.Storage.GetAllUrlCacheEntriesAsync();
                foreach (var entry in allCached)
                {
                    if (!string.IsNullOrEmpty(entry.ETag) || !string.IsNullOrEmpty(entry.LastModified))
                        urlCacheLookup[entry.Url] = (entry.ETag, entry.LastModified);
                }
            }

            var newItems = new List<ContentItem>();
            var urlCachedItems = new List<ContentItem>();
            var httpNotModifiedCount = 0;
            var contentHashCachedCount = 0;
            var capturedHeaders = new Dictionary<string, (string? etag, string? lastModified)>(StringComparer.OrdinalIgnoreCase);

            // Stage 1: Crawl
            Func<string, (string? etag, string? lastModified)>? cacheProvider =
                settings.Force ? null : url =>
                {
                    var normalized = url.Split('?')[0].Split('#')[0].TrimEnd('/').ToLowerInvariant();
                    return urlCacheLookup.TryGetValue(normalized, out var cached) ? cached : (null, null);
                };

            await writer.WriteAsync(new CrawlProgressUpdate(
                CrawlStage.Crawling, "Starting crawl...", 0, settings.MaxPages), ct);

            await foreach (var result in crawler.CrawlAsync(
                settings.Url,
                cacheProvider: cacheProvider,
                progress: new Progress<(int visited, int queued, int extracted)>(p =>
                {
                    writer.TryWrite(new CrawlProgressUpdate(
                        CrawlStage.Crawling,
                        $"Crawling ({p.visited} visited, {p.extracted} extracted, {p.queued} queued)",
                        p.visited, settings.MaxPages));
                }),
                ct: ct))
            {
                if (result.Url != null && (result.ETag != null || result.LastModified != null))
                    capturedHeaders[result.Url] = (result.ETag, result.LastModified);

                if (result.NotModified)
                {
                    httpNotModifiedCount++;
                    if (!string.IsNullOrEmpty(result.Url))
                        await boot.Storage.UpdateUrlCacheAsync(result.Url, null, result.ETag, result.LastModified, 0);
                    continue;
                }

                var item = result.Item;
                if (item == null) continue;

                if (!settings.Force && !string.IsNullOrEmpty(item.Url) && !string.IsNullOrEmpty(item.Content))
                {
                    var contentHash = ItemProcessor.ComputeContentHash(item.Content);
                    if (await boot.Storage.IsContentUnchangedAsync(item.Url, contentHash))
                    {
                        contentHashCachedCount++;
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
            writer.TryWrite(new CrawlProgressUpdate(
                CrawlStage.Crawling,
                $"Crawl complete: {crawler.PagesVisited} pages, {newItems.Count} new/changed, {totalCached} cached",
                settings.MaxPages, settings.MaxPages));

            if (newItems.Count == 0)
            {
                var msg = $"All {totalCached} pages unchanged — nothing to index";
                await writer.WriteAsync(new CrawlProgressUpdate(
                    CrawlStage.Completed, msg, IsComplete: true), ct);
                return new CrawlSessionResult(
                    crawler.PagesVisited, crawler.PagesExtracted, crawler.PagesSkipped,
                    0, httpNotModifiedCount, contentHashCachedCount,
                    crawler.RetryCount, crawler.AdaptiveDelayMs, kbName);
            }

            // Stage 2: Compute embeddings
            for (var i = 0; i < newItems.Count; i++)
            {
                var item = newItems[i];
                var textToEmbed = ItemProcessor.PrepareEmbeddingText(item.Title, item.Content);
                item.Embedding = await boot.Embedding.EmbedAsync(textToEmbed, ct);

                if ((i + 1) % 5 == 0 || i == newItems.Count - 1)
                {
                    writer.TryWrite(new CrawlProgressUpdate(
                        CrawlStage.Embedding,
                        $"Computing embeddings {i + 1}/{newItems.Count}",
                        i + 1, newItems.Count));
                }
            }

            using var processor = await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, boot.EntityStore, collectionName: kbName, ct: ct);

            // Stage 3: NER entity extraction (optional)
            var articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
            if (!settings.NoEntities)
            {
                using var nerService = new NerService();
                if (nerService.IsAvailable)
                {
                    await nerService.InitializeAsync();
                    for (var i = 0; i < newItems.Count; i++)
                    {
                        var item = newItems[i];
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

                        if ((i + 1) % 5 == 0 || i == newItems.Count - 1)
                        {
                            writer.TryWrite(new CrawlProgressUpdate(
                                CrawlStage.ExtractingEntities,
                                $"Extracting entities {i + 1}/{newItems.Count}",
                                i + 1, newItems.Count));
                        }
                    }
                }
            }

            // Stage 4: Store in knowledge base + update URL cache
            for (var i = 0; i < newItems.Count; i++)
            {
                var item = newItems[i];
                processor.ScoreSentimentAndTopic(item);

                item.Summary ??= item.Content?.Length > 300
                    ? item.Content[..300] + "..."
                    : item.Content ?? item.Title;

                await processor.IndexItemAsync(item);

                if (!string.IsNullOrEmpty(item.Url) && !string.IsNullOrEmpty(item.Content))
                {
                    var contentHash = ItemProcessor.ComputeContentHash(item.Content);
                    var (etag, lastMod) = capturedHeaders.TryGetValue(item.Url, out var h) ? h : (null, null);
                    await boot.Storage.UpdateUrlCacheAsync(item.Url, contentHash, etag, lastMod, item.Content.Length);
                }

                Interlocked.Increment(ref sharedCounter[0]);

                if ((i + 1) % 3 == 0 || i == newItems.Count - 1)
                {
                    writer.TryWrite(new CrawlProgressUpdate(
                        CrawlStage.Saving,
                        $"Saving to KB '{kbName}' {i + 1}/{newItems.Count}",
                        i + 1, newItems.Count, Volatile.Read(ref sharedCounter[0])));
                }
            }

            // Compensate: URL-cached items need Lucene indexing for this collection.
            // Also backfill from storage to keep the index populated.
            if (urlCachedItems.Count > 0)
            {
                processor.EnsureInLucene(urlCachedItems);

                var sourceTag = $"crawl:{kbName}";
                var shortfall = Math.Min(urlCachedItems.Count, settings.MaxPages - newItems.Count);
                if (shortfall > 0)
                {
                    var storedItems = await boot.Storage.GetItemsBySourceAsync(sourceTag, limit: shortfall);
                    processor.EnsureInLucene(storedItems.Select(s => s.ToContentItem()));
                }

                writer.TryWrite(new CrawlProgressUpdate(
                    CrawlStage.Saving,
                    $"Backfilled {urlCachedItems.Count} cached items into Lucene",
                    newItems.Count, newItems.Count, Volatile.Read(ref sharedCounter[0])));
            }

            // Stage 5: Persist entities
            if (articleEntityMap.Count > 0)
            {
                for (var i = 0; i < articleEntityMap.Count; i++)
                {
                    var (item, entities) = articleEntityMap[i];
                    await processor.PersistEntitiesAsync(item, entities);

                    if ((i + 1) % 5 == 0 || i == articleEntityMap.Count - 1)
                    {
                        writer.TryWrite(new CrawlProgressUpdate(
                            CrawlStage.PersistingEntities,
                            $"Persisting entities {i + 1}/{articleEntityMap.Count}",
                            i + 1, articleEntityMap.Count, Volatile.Read(ref sharedCounter[0])));
                    }
                }
            }

            var completionMsg = $"{newItems.Count} pages indexed" +
                (totalCached > 0 ? $" ({totalCached} cached)" : "") +
                (crawler.PagesSkipped > 0 ? $" ({crawler.PagesSkipped} skipped)" : "");

            await writer.WriteAsync(new CrawlProgressUpdate(
                CrawlStage.Completed, completionMsg, ItemsIndexed: Volatile.Read(ref sharedCounter[0]), IsComplete: true), ct);

            return new CrawlSessionResult(
                crawler.PagesVisited, crawler.PagesExtracted, crawler.PagesSkipped,
                newItems.Count, httpNotModifiedCount, contentHashCachedCount,
                crawler.RetryCount, crawler.AdaptiveDelayMs, kbName);
        }
        catch (OperationCanceledException)
        {
            writer.TryWrite(new CrawlProgressUpdate(
                CrawlStage.Failed, "Crawl cancelled", ItemsIndexed: Volatile.Read(ref sharedCounter[0]), IsComplete: true));
            throw;
        }
        catch (Exception ex)
        {
            writer.TryWrite(new CrawlProgressUpdate(
                CrawlStage.Failed, $"Crawl failed: {ex.Message}", ItemsIndexed: Volatile.Read(ref sharedCounter[0]), IsComplete: true, Error: ex));
            throw;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _internalCts.Cancel();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Timeout or cancellation — ok
        }
        _linkedCts.Dispose();
        _internalCts.Dispose();
    }
}

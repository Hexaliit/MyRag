using System.Diagnostics;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using DoomWriter.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomWriter.Services;

public class CrawlService : IDisposable
{
    private readonly IEmbeddingService _embedding;
    private readonly IEntityGraphStore _entityGraph;
    private readonly NerService _ner;
    private readonly WriterSettingsService _settings;
    private readonly StorageService _storage;
    private readonly DuckDbVectorStore _vectorStore;

    private CancellationTokenSource? _crawlCts;
    private LuceneSearchService? _lucene;

    public CrawlService(
        IEmbeddingService embedding,
        StorageService storage,
        DuckDbVectorStore vectorStore,
        IEntityGraphStore entityGraph,
        NerService ner,
        WriterSettingsService settings)
    {
        _embedding = embedding;
        _storage = storage;
        _vectorStore = vectorStore;
        _entityGraph = entityGraph;
        _ner = ner;
        _settings = settings;
    }

    public bool IsCrawling { get; private set; }

    public void Dispose()
    {
        _crawlCts?.Cancel();
        _crawlCts?.Dispose();
    }

    public event EventHandler<CrawlProgressUpdate>? CrawlProgress;
    public event EventHandler<CrawlSessionResult>? CrawlCompleted;

    public void SetLucene(LuceneSearchService lucene)
    {
        _lucene = lucene;
    }

    public async Task StartCrawlAsync(CrawlSource source, CancellationToken ct = default)
    {
        if (IsCrawling) return;
        IsCrawling = true;

        _crawlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _crawlCts.Token;

        try
        {
            var config = new CrawlConfig
            {
                Name = string.IsNullOrEmpty(source.Name)
                    ? new Uri(source.Url).Host
                    : source.Name,
                MaxDepth = source.Recurse ? source.MaxDepth : 1,
                MaxPages = source.MaxPages,
                PathFilter = source.PathFilter
            };

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DoomWriter/1.0 (Knowledge Crawler)");

            var crawler = new WebCrawlerService(httpClient, config);

            var itemsIndexed = 0;
            var totalVisited = 0;

            CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                CrawlStage.Crawling, $"Starting crawl of {source.Url}"));

            await foreach (var result in crawler.CrawlAsync(
                               source.Url,
                               url => GetCacheForUrl(url),
                               ct: token))
            {
                totalVisited++;

                if (result.NotModified || result.Item == null)
                {
                    CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                        CrawlStage.Crawling,
                        $"Not modified: {result.Url}",
                        totalVisited,
                        config.MaxPages));
                    continue;
                }

                var item = result.Item;

                // Embed
                CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                    CrawlStage.Embedding,
                    $"Embedding: {item.Title}",
                    totalVisited,
                    config.MaxPages,
                    itemsIndexed));

                try
                {
                    var embedding = await _embedding.EmbedAsync(
                        item.Content ?? item.Title, token);
                    item.Embedding = embedding;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Embedding failed for {item.Url}: {ex.Message}");
                    continue;
                }

                // Store in DuckDB HNSW
                if (item.Embedding != null)
                    await _vectorStore.UpsertItemEmbeddingAsync(
                        item.Id, item.Title, item.Source, item.Url, item.Embedding);

                // Store in SQLite
                CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                    CrawlStage.Saving,
                    $"Saving: {item.Title}",
                    totalVisited,
                    config.MaxPages,
                    itemsIndexed));

                await _storage.SaveItemAsync(item);

                // Index in Lucene FTS
                _lucene?.IndexItem(item);

                // Update URL cache
                await _storage.UpdateUrlCacheAsync(
                    result.Url,
                    null,
                    result.ETag,
                    result.LastModified,
                    item.Content?.Length ?? 0);

                // NER extraction
                if (_ner.IsAvailable && !string.IsNullOrEmpty(item.Content))
                {
                    CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                        CrawlStage.ExtractingEntities,
                        $"Extracting entities: {item.Title}",
                        totalVisited,
                        config.MaxPages,
                        itemsIndexed));

                    try
                    {
                        var entities = await _ner.ExtractEntitiesAsync(item.Content, token);
                        if (entities.Count > 0) await PersistCrawlEntitiesAsync(item.Id, item.Title, entities);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"NER failed for {item.Url}: {ex.Message}");
                    }
                }

                itemsIndexed++;
            }

            _lucene?.Commit();

            source.LastCrawled = DateTimeOffset.UtcNow;
            _settings.Save();

            var sessionResult = new CrawlSessionResult(
                crawler.PagesVisited,
                crawler.PagesExtracted,
                crawler.PagesSkipped,
                itemsIndexed,
                crawler.PagesNotModified,
                0,
                crawler.RetryCount,
                crawler.AdaptiveDelayMs,
                config.Name);

            CrawlCompleted?.Invoke(this, sessionResult);

            CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                CrawlStage.Completed,
                $"Crawl complete. {itemsIndexed} pages indexed.",
                totalVisited,
                totalVisited,
                itemsIndexed,
                true));
        }
        catch (OperationCanceledException)
        {
            CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                CrawlStage.Completed,
                "Crawl cancelled.",
                IsComplete: true));
        }
        catch (Exception ex)
        {
            CrawlProgress?.Invoke(this, new CrawlProgressUpdate(
                CrawlStage.Failed,
                $"Crawl failed: {ex.Message}",
                IsComplete: true,
                Error: ex));
        }
        finally
        {
            IsCrawling = false;
        }
    }

    public void CancelCrawl()
    {
        _crawlCts?.Cancel();
    }

    private (string? etag, string? lastModified) GetCacheForUrl(string url)
    {
        var entry = _storage.GetUrlCacheAsync(url).GetAwaiter().GetResult();
        return (entry?.ETag, entry?.LastModified);
    }

    private async Task PersistCrawlEntitiesAsync(
        string itemId, string title, List<NerEntity> entities)
    {
        var deduped = entities
            .GroupBy(e => e.Text.ToLowerInvariant())
            .Select(g => g.MaxBy(e => e.Confidence)!)
            .ToList();

        var entityIds = new List<string>();

        foreach (var entity in deduped)
        {
            var entityId = KnowledgeGraphService.GenerateEntityId(entity.Text, entity.Type);
            entityIds.Add(entityId);

            await _entityGraph.UpsertEntityAsync(
                entityId, entity.Text, entity.Type, (float)entity.Confidence);
            await _entityGraph.UpsertEntityMentionAsync(
                entityId, itemId, (float)entity.Confidence, title);
        }

        for (var i = 0; i < entityIds.Count; i++)
        for (var j = i + 1; j < entityIds.Count; j++)
            await _entityGraph.UpsertRelationshipAsync(
                entityIds[i], entityIds[j]);
    }
}
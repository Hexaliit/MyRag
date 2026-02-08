using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Channel-based queue for papers fetched but not fully ingested.
///     Processes at lower priority after main loop completes or on early-exit.
///     Uses ml lane with maxConcurrency=1 to avoid ONNX contention.
/// </summary>
public sealed class BackgroundReindexCoordinator : IDisposable
{
    private readonly IDocumentIngester _ingester;
    private readonly ILogger<BackgroundReindexCoordinator> _logger;
    private readonly Channel<(string FilePath, Guid CollectionId)> _queue;
    private CancellationTokenSource? _cts;
    private Task? _processingTask;

    public BackgroundReindexCoordinator(
        IDocumentIngester ingester,
        ILogger<BackgroundReindexCoordinator> logger)
    {
        _ingester = ingester;
        _logger = logger;
        _queue = Channel.CreateBounded<(string, Guid)>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public int QueuedCount => _queue.Reader.Count;

    /// <summary>
    ///     Enqueue a paper for background reindexing.
    /// </summary>
    public bool Enqueue(string filePath, Guid collectionId)
    {
        return _queue.Writer.TryWrite((filePath, collectionId));
    }

    /// <summary>
    ///     Start processing the queue in the background.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        if (_processingTask != null) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _processingTask = Task.Run(async () => await ProcessQueueAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    ///     Stop processing and wait for completion.
    /// </summary>
    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        _cts?.Cancel();

        if (_processingTask != null)
        {
            try { await _processingTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        _logger.LogInformation("BackgroundReindexCoordinator started, processing queued papers");

        await foreach (var (filePath, collectionId) in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                var result = await _ingester.IngestAsync(filePath, collectionId, ct);
                if (result.Success)
                {
                    _logger.LogDebug("Background reindex succeeded: {FilePath} ({Segments} segments)",
                        filePath, result.SegmentCount);
                }
                else
                {
                    _logger.LogWarning("Background reindex failed: {FilePath}: {Message}", filePath, result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background reindex error for {FilePath}", filePath);
            }
        }

        _logger.LogInformation("BackgroundReindexCoordinator stopped");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

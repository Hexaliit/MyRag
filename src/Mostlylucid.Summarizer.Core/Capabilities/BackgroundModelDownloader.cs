using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizer.Core.Capabilities;

/// <summary>
///     Background coordinator for lazy model downloads.
///     Downloads models only when first needed by a component.
///     Emits signals when models become available.
/// </summary>
public class BackgroundModelDownloader : IDisposable
{
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    // Track download states
    private readonly ConcurrentDictionary<string, DownloadState> _downloadStates = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger<BackgroundModelDownloader> _logger;
    private readonly string _modelsDirectory;

    // Pending download requests (deduplicated)
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ModelDownloadResult>> _pendingDownloads = new();
    private readonly CapabilityRegistry _registry;
    private readonly ICapabilitySignalSink _signalSink;

    public BackgroundModelDownloader(
        ILogger<BackgroundModelDownloader> logger,
        CapabilityRegistry registry,
        ICapabilitySignalSink signalSink,
        string? modelsDirectory = null)
    {
        _logger = logger;
        _registry = registry;
        _signalSink = signalSink;
        _modelsDirectory = modelsDirectory ?? GetDefaultModelsDirectory();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        Directory.CreateDirectory(_modelsDirectory);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _downloadLock.Dispose();
    }

    /// <summary>
    ///     Request a model, downloading if necessary. Returns immediately if already downloaded.
    ///     Multiple requests for the same model are deduplicated.
    /// </summary>
    public async Task<ModelDownloadResult> EnsureModelAsync(string modelId, CancellationToken ct = default)
    {
        var model = ModelManifest.Instance.GetModel(modelId);
        if (model == null)
            return new ModelDownloadResult
            {
                ModelId = modelId,
                Success = false,
                Error = $"Unknown model: {modelId}"
            };

        var localPath = Path.Combine(_modelsDirectory, model.RelativePath);

        // Already downloaded?
        if (File.Exists(localPath))
        {
            _logger.LogDebug("Model {ModelId} already exists at {Path}", modelId, localPath);
            return new ModelDownloadResult
            {
                ModelId = modelId,
                Success = true,
                LocalPath = localPath,
                AlreadyExisted = true
            };
        }

        // Check if download already in progress
        var tcs = new TaskCompletionSource<ModelDownloadResult>();
        var existingTcs = _pendingDownloads.GetOrAdd(modelId, tcs);

        if (existingTcs != tcs)
        {
            // Another request is already downloading this model
            _logger.LogDebug("Waiting for existing download of {ModelId}", modelId);
            return await existingTcs.Task.WaitAsync(ct);
        }

        // We're the first request - start the download
        try
        {
            var result = await DownloadModelAsync(model, localPath, ct);
            tcs.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            var errorResult = new ModelDownloadResult
            {
                ModelId = modelId,
                Success = false,
                Error = ex.Message
            };
            tcs.SetResult(errorResult);
            return errorResult;
        }
        finally
        {
            _pendingDownloads.TryRemove(modelId, out _);
        }
    }

    /// <summary>
    ///     Get all models required by a component.
    /// </summary>
    public async Task<List<ModelDownloadResult>> EnsureModelsForComponentAsync(
        string componentName,
        CancellationToken ct = default)
    {
        var models = ModelManifest.Instance.GetModelsForComponent(componentName).ToList();
        var results = new List<ModelDownloadResult>();

        foreach (var model in models)
        {
            var result = await EnsureModelAsync(model.Id, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    ///     Check which models are available locally (no download).
    /// </summary>
    public Dictionary<string, bool> CheckModelAvailability()
    {
        var availability = new Dictionary<string, bool>();

        foreach (var (modelId, model) in ModelManifest.Instance.Models)
        {
            var localPath = Path.Combine(_modelsDirectory, model.RelativePath);
            availability[modelId] = File.Exists(localPath);
        }

        return availability;
    }

    /// <summary>
    ///     Get download state for a model (if downloading).
    /// </summary>
    public DownloadState? GetDownloadState(string modelId)
    {
        return _downloadStates.GetValueOrDefault(modelId);
    }

    private async Task<ModelDownloadResult> DownloadModelAsync(
        ModelDefinition model,
        string localPath,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting download of {ModelId} from {Url}", model.Id, model.DownloadUrl);

        // Emit signal: download starting
        await _signalSink.EmitAsync(new CapabilitySignal
        {
            SignalType = CapabilitySignalType.ModelDownloadStarted,
            ModelId = model.Id,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        var state = new DownloadState
        {
            ModelId = model.Id,
            Status = DownloadStatus.Downloading,
            StartedAt = DateTimeOffset.UtcNow,
            ExpectedBytes = model.ExpectedSizeBytes
        };
        _downloadStates[model.Id] = state;

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Download with progress
            using var response =
                await _httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? model.ExpectedSizeBytes ?? 0;
            state.ExpectedBytes = totalBytes;

            var tempPath = localPath + ".tmp";
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream =
                         new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                var totalRead = 0L;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;

                    state.DownloadedBytes = totalRead;
                    state.Progress = totalBytes > 0 ? (double)totalRead / totalBytes : 0;

                    // Emit progress every 10%
                    if (totalBytes > 0 && totalRead % (totalBytes / 10 + 1) < bytesRead)
                        await _signalSink.EmitAsync(new CapabilitySignal
                        {
                            SignalType = CapabilitySignalType.ModelDownloadProgress,
                            ModelId = model.Id,
                            Progress = state.Progress,
                            Timestamp = DateTimeOffset.UtcNow
                        }, ct);
                }
            }

            // Verify hash if provided
            if (!string.IsNullOrEmpty(model.Sha256Hash))
            {
                state.Status = DownloadStatus.Verifying;
                var hash = await ComputeFileHashAsync(tempPath, ct);

                if (!string.Equals(hash, model.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"Hash mismatch for {model.Id}. Expected: {model.Sha256Hash}, Got: {hash}");
                }
            }

            // Move to final location
            File.Move(tempPath, localPath, true);

            state.Status = DownloadStatus.Completed;
            state.CompletedAt = DateTimeOffset.UtcNow;
            state.Progress = 1.0;

            _logger.LogInformation("Successfully downloaded {ModelId} to {Path}", model.Id, localPath);

            // Emit signal: download completed
            await _signalSink.EmitAsync(new CapabilitySignal
            {
                SignalType = CapabilitySignalType.ModelAvailable,
                ModelId = model.Id,
                LocalPath = localPath,
                Timestamp = DateTimeOffset.UtcNow
            }, ct);

            // Refresh capability registry
            await _registry.RefreshAsync(ct);

            return new ModelDownloadResult
            {
                ModelId = model.Id,
                Success = true,
                LocalPath = localPath,
                DownloadedBytes = state.DownloadedBytes
            };
        }
        catch (Exception ex)
        {
            state.Status = DownloadStatus.Failed;
            state.Error = ex.Message;

            _logger.LogError(ex, "Failed to download {ModelId}", model.Id);

            // Emit signal: download failed
            await _signalSink.EmitAsync(new CapabilitySignal
            {
                SignalType = CapabilitySignalType.ModelDownloadFailed,
                ModelId = model.Id,
                Error = ex.Message,
                Timestamp = DateTimeOffset.UtcNow
            }, ct);

            throw;
        }
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetDefaultModelsDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "lucidrag", "models");
    }
}

/// <summary>
///     Result of a model download request.
/// </summary>
public record ModelDownloadResult
{
    public required string ModelId { get; init; }
    public bool Success { get; init; }
    public string? LocalPath { get; init; }
    public string? Error { get; init; }
    public bool AlreadyExisted { get; init; }
    public long DownloadedBytes { get; init; }
}

/// <summary>
///     Current state of a model download.
/// </summary>
public class DownloadState
{
    public required string ModelId { get; init; }
    public DownloadStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? ExpectedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double Progress { get; set; }
    public string? Error { get; set; }
}

/// <summary>
///     Download status enum.
/// </summary>
public enum DownloadStatus
{
    Pending,
    Downloading,
    Verifying,
    Completed,
    Failed
}
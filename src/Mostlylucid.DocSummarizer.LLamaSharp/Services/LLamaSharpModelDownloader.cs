using Mostlylucid.DocSummarizer.LLamaSharp.Config;

namespace Mostlylucid.DocSummarizer.LLamaSharp.Services;

/// <summary>
///     Downloads GGUF model files from HuggingFace with atomic temp-file pattern.
/// </summary>
public sealed class LLamaSharpModelDownloader : IDisposable
{
    private readonly LLamaSharpConfig _config;
    private readonly HttpClient _httpClient;

    public LLamaSharpModelDownloader(LLamaSharpConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DocSummarizer-LLamaSharp/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>
    ///     Ensure a model is downloaded and return its local file path.
    ///     If the model already exists, returns immediately.
    /// </summary>
    public async Task<string> EnsureModelAsync(
        LLamaSharpModelRegistry.ModelInfo model,
        IProgress<(long downloaded, long total, string description)>? progress = null,
        CancellationToken ct = default)
    {
        var modelDir = _config.ResolvedModelDirectory;
        Directory.CreateDirectory(modelDir);

        var localPath = Path.Combine(modelDir, model.Filename);

        if (File.Exists(localPath))
        {
            var fileInfo = new FileInfo(localPath);
            // Basic size validation (allow 10% tolerance for different quantization builds)
            if (fileInfo.Length > model.ExpectedSizeBytes * 0.5)
                return localPath;
        }

        if (!_config.AutoDownload)
            throw new InvalidOperationException(
                $"Model {model.Name} not found at {localPath} and auto-download is disabled. " +
                $"Download manually from https://huggingface.co/{model.HuggingFaceRepo}");

        await DownloadFileAsync(
            LLamaSharpModelRegistry.GetDownloadUrl(model),
            localPath,
            model.DisplayName,
            model.ExpectedSizeBytes,
            progress,
            ct);

        return localPath;
    }

    /// <summary>
    ///     Check if a model file exists locally.
    /// </summary>
    public bool IsModelAvailable(LLamaSharpModelRegistry.ModelInfo model)
    {
        var localPath = Path.Combine(_config.ResolvedModelDirectory, model.Filename);
        return File.Exists(localPath) && new FileInfo(localPath).Length > model.ExpectedSizeBytes * 0.5;
    }

    /// <summary>
    ///     Get the local path for a model (may not exist yet).
    /// </summary>
    public string GetModelPath(LLamaSharpModelRegistry.ModelInfo model)
    {
        return Path.Combine(_config.ResolvedModelDirectory, model.Filename);
    }

    private async Task DownloadFileAsync(
        string url,
        string localPath,
        string description,
        long expectedSize,
        IProgress<(long downloaded, long total, string description)>? progress,
        CancellationToken ct)
    {
        var tempPath = localPath + ".tmp";

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

            // Write to stderr so we don't pollute JSON output
            Console.Error.WriteLine($"Downloading {description} ({totalBytes / 1_000_000} MB)...");

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream =
                         new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                    progress?.Report((totalRead, totalBytes, description));
                }
            }

            // Atomic move after streams are closed
            File.Move(tempPath, localPath, true);
            Console.Error.WriteLine($"Downloaded {description} to {localPath}");
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }
}
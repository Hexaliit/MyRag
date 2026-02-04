using Mostlylucid.Summarizer.Core.Capabilities;

namespace AudioSummarizer.Core.Services.SourceSeparation;

/// <summary>
///     Downloads and manages the Demucs ONNX model for source separation.
///     Model source: HuggingFace gentij/htdemucs-ort (~210MB)
///     Uses the central ModelManifest for model definitions and paths.
/// </summary>
public class DemucsModelDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DemucsModelDownloader> _logger;
    private readonly ModelDefinition _modelDefinition;

    public DemucsModelDownloader(
        ILogger<DemucsModelDownloader> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        // Get model definition from central manifest
        _modelDefinition = ModelManifest.Instance.GetModel(ModelIds.HtDemucs)
                           ?? throw new InvalidOperationException("HTDemucs model not found in ModelManifest");

        // Use centralized models directory
        ModelPath = Path.Combine(ModelManifest.Instance.ModelsDirectory, _modelDefinition.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
    }

    public string ModelPath { get; }

    public bool IsModelAvailable => File.Exists(ModelPath) &&
                                    new FileInfo(ModelPath).Length >
                                    (_modelDefinition.ExpectedSizeBytes ?? 100_000_000) / 2;

    /// <summary>
    ///     Ensure the Demucs model is downloaded
    /// </summary>
    public async Task EnsureModelDownloadedAsync(CancellationToken cancellationToken = default)
    {
        if (IsModelAvailable)
        {
            _logger.LogDebug("Demucs model already exists at {ModelPath}", ModelPath);
            return;
        }

        _logger.LogInformation("Downloading {ModelName} (~{Size}MB) from HuggingFace...",
            _modelDefinition.Name,
            (_modelDefinition.ExpectedSizeBytes ?? 220_000_000) / 1024 / 1024);

        var client = _httpClientFactory.CreateClient("HuggingFace");

        var tempPath = ModelPath + ".tmp";

        try
        {
            using var response = await client.GetAsync(_modelDefinition.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ??
                             _modelDefinition.ExpectedSizeBytes ?? 220_000_000;

            // Download to temp file
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream =
                         new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalBytesRead = 0;
                int bytesRead;
                var lastProgress = 0;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    var progress = (int)(totalBytesRead * 100 / totalBytes);
                    if (progress >= lastProgress + 10)
                    {
                        _logger.LogInformation("Demucs download progress: {Progress}% ({MB:F1}MB)",
                            progress, totalBytesRead / 1024.0 / 1024.0);
                        lastProgress = progress;
                    }
                }
            }

            // Move temp file to final location (streams are now closed)
            if (File.Exists(ModelPath))
                File.Delete(ModelPath);
            File.Move(tempPath, ModelPath);

            _logger.LogInformation("Demucs model downloaded successfully to {ModelPath}", ModelPath);
        }
        catch (Exception ex)
        {
            // Clean up temp file on error
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                /* Ignore cleanup errors */
            }

            _logger.LogError(ex, "Failed to download Demucs model from {Url}", _modelDefinition.DownloadUrl);
            throw new InvalidOperationException($"Failed to download Demucs model: {ex.Message}", ex);
        }
    }
}
using AudioSummarizer.Core.Config;

namespace AudioSummarizer.Core.Services.Voice;

/// <summary>
/// Downloads ECAPA-TDNN ONNX model for voice embeddings
/// </summary>
public class VoiceEmbeddingModelDownloader
{
    private readonly ILogger<VoiceEmbeddingModelDownloader> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _modelPath;

    public VoiceEmbeddingModelDownloader(
        ILogger<VoiceEmbeddingModelDownloader> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<AudioConfig> config)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("HuggingFace");
        _modelPath = config.Value.VoiceEmbedding.ModelPath;
    }

    /// <summary>
    /// Download ECAPA-TDNN model if not already present
    /// </summary>
    public async Task EnsureModelDownloadedAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_modelPath))
        {
            _logger.LogInformation("ECAPA-TDNN model already exists at {ModelPath}", _modelPath);
            return;
        }

        var modelDir = Path.GetDirectoryName(_modelPath);
        if (!Directory.Exists(modelDir))
        {
            Directory.CreateDirectory(modelDir!);
        }

        _logger.LogInformation("Downloading ECAPA-TDNN model to {ModelPath}...", _modelPath);

        // Download from HuggingFace
        const string modelUrl = "https://huggingface.co/Wespeaker/wespeaker-ecapa-tdnn512-LM/resolve/main/voxceleb_ECAPA512_LM.onnx";

        try
        {
            using var response = await _httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            _logger.LogInformation("Model size: {Size} MB", totalBytes / 1024.0 / 1024.0);

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(_modelPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var progress = (double)totalRead / totalBytes * 100;
                    if (totalRead % (1024 * 1024) < 8192) // Log every MB
                    {
                        _logger.LogInformation("Download progress: {Progress:F1}%", progress);
                    }
                }
            }

            _logger.LogInformation("ECAPA-TDNN model downloaded successfully ({Size} MB)", totalRead / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download ECAPA-TDNN model from {Url}", modelUrl);

            // Clean up partial download
            if (File.Exists(_modelPath))
            {
                File.Delete(_modelPath);
            }

            throw new InvalidOperationException($"Failed to download voice embedding model: {ex.Message}", ex);
        }
    }
}

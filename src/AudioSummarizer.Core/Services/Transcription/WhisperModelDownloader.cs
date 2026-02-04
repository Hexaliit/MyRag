namespace AudioSummarizer.Core.Services.Transcription;

/// <summary>
///     Downloads and caches Whisper ONNX models from HuggingFace
/// </summary>
public class WhisperModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhisperModelDownloader> _logger;

    public WhisperModelDownloader(ILogger<WhisperModelDownloader> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AudioSummarizer/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(30); // Large models can take time
    }

    /// <summary>
    ///     Ensure Whisper model is downloaded and return local path
    /// </summary>
    public async Task<string> EnsureModelAsync(
        string modelPath,
        string modelSize,
        string language,
        CancellationToken ct = default)
    {
        if (File.Exists(modelPath))
        {
            _logger.LogDebug("Whisper model already exists at {ModelPath}", modelPath);
            return modelPath;
        }

        // Create directory if needed
        var modelDir = Path.GetDirectoryName(modelPath);
        if (!string.IsNullOrEmpty(modelDir)) Directory.CreateDirectory(modelDir);

        // Determine model URL
        var url = GetModelUrl(modelSize, language);
        var fileName = GetModelFileName(modelSize, language);

        _logger.LogInformation("Downloading Whisper {ModelSize} model from HuggingFace (~{Size}MB)...",
            modelSize, GetModelSizeMB(modelSize));
        _logger.LogInformation("Model will be cached at: {ModelPath}", modelPath);

        await DownloadFileAsync(url, modelPath, ct);

        _logger.LogInformation("Whisper model downloaded successfully!");
        return modelPath;
    }

    private async Task DownloadFileAsync(
        string url,
        string localPath,
        CancellationToken ct)
    {
        var tempPath = localPath + ".tmp";

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream =
                         new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        var progress = (int)(totalRead * 100 / totalBytes);
                        if (progress % 10 == 0) // Log every 10%
                            _logger.LogDebug("Download progress: {Progress}%", progress);
                    }
                }
            }

            // Move after streams are closed
            File.Move(tempPath, localPath, true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private static string GetModelUrl(string modelSize, string language)
    {
        // HuggingFace URLs for Whisper GGML models
        var baseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";
        var fileName = GetModelFileName(modelSize, language);
        return $"{baseUrl}/{fileName}";
    }

    private static string GetModelFileName(string modelSize, string language)
    {
        var size = modelSize.ToLowerInvariant();
        var lang = language.ToLowerInvariant();

        // English-specific models are smaller and faster
        if (lang == "en")
            return size switch
            {
                "tiny" => "ggml-tiny.en.bin",
                "base" => "ggml-base.en.bin",
                "small" => "ggml-small.en.bin",
                "medium" => "ggml-medium.en.bin",
                _ => "ggml-base.en.bin"
            };

        // Multi-language models
        return size switch
        {
            "tiny" => "ggml-tiny.bin",
            "base" => "ggml-base.bin",
            "small" => "ggml-small.bin",
            "medium" => "ggml-medium.bin",
            "large" => "ggml-large-v3.bin",
            _ => "ggml-base.bin"
        };
    }

    private static int GetModelSizeMB(string modelSize)
    {
        return modelSize.ToLowerInvariant() switch
        {
            "tiny" => 75,
            "base" => 142,
            "small" => 466,
            "medium" => 1530,
            "large" => 3090,
            _ => 142
        };
    }
}
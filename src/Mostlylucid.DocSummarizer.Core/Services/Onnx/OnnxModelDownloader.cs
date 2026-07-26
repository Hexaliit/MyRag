using System.Net.Http;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Services.Onnx;

/// <summary>
///     Downloads and caches ONNX models from HuggingFace
/// </summary>
public class OnnxModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _modelDirectory;
    private readonly bool _verbose;

    public OnnxModelDownloader(OnnxConfig config, bool verbose = false)
    {
        _modelDirectory = config.ModelDirectory;
        _verbose = verbose;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DocSummarizer/3.0");
    }

    /// <summary>
    ///     Ensure embedding model is downloaded and return local path
    /// </summary>
    public async Task<EmbeddingModelPaths> EnsureEmbeddingModelAsync(
        EmbeddingModelInfo model,
        CancellationToken ct = default)
    {
        var modelDir = Path.Combine(_modelDirectory, "embeddings", SanitizeName(model.Name));
        Directory.CreateDirectory(modelDir);

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        var tasks = new List<Task>();

        if (!File.Exists(modelPath))
            tasks.Add(DownloadFileAsync(model.GetModelUrl(), modelPath, $"Downloading {model.Name} model",
                model.SizeBytes, ct));

        if (!File.Exists(tokenizerPath))
            tasks.Add(DownloadFileAsync(model.GetTokenizerUrl(), tokenizerPath, "Downloading tokenizer", null, ct));

        if (!File.Exists(vocabPath))
            tasks.Add(DownloadFileAsync(model.GetVocabUrl(), vocabPath, "Downloading vocab", null, ct));

        if (tasks.Count > 0)
        {
            VerboseHelper.Log(
                $"[yellow]First run: downloading ONNX embedding model {model.Name} (~{model.SizeBytes / 1_000_000}MB)...[/]");
            Console.Error.WriteLine($"Models are cached at: {modelDir}");

            try
            {
                await Task.WhenAll(tasks);
                Console.Error.WriteLine("Model downloaded successfully!");
            }
            catch (HttpRequestException ex)
            {
                var msg = $"""
                    Cannot download ONNX embedding model '{model.Name}' from HuggingFace.
                    Network error: {ex.Message}

                    To run offline:
                    1. On a machine WITH internet, download these files:
                       {model.GetModelUrl()}
                       {model.GetTokenizerUrl()}
                       {model.GetVocabUrl()}
                    2. Copy them to: {modelDir}
                       (rename model file to "model.onnx")

                    Or to use Ollama instead, set "EmbeddingBackend": "Ollama" in appsettings.json
                    """;
                throw new InvalidOperationException(msg);
            }
        }

        return new EmbeddingModelPaths(modelPath, tokenizerPath, vocabPath);
    }

    private async Task DownloadFileAsync(
        string url,
        string localPath,
        string description,
        long? expectedSize,
        CancellationToken ct)
    {
        var tempPath = localPath + ".tmp";

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize ?? 0;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream =
                         new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                // Avoid Spectre progress here to prevent concurrent interactive displays.
                // Simply stream the download to disk; verbose info is handled above.
                await contentStream.CopyToAsync(fileStream, ct);
            }

            // Move after streams are closed
            File.Move(tempPath, localPath, true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static string SanitizeName(string name)
    {
        return name.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
    }
}

/// <summary>
///     Paths to downloaded embedding model files
/// </summary>
public record EmbeddingModelPaths(string ModelPath, string TokenizerPath, string VocabPath);
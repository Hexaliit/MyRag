using DomainClassifier.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainClassifier.Core.Services.Onnx;

/// <summary>
///     Downloads and caches ONNX classifier models from HuggingFace.
///     Mirrors OnnxModelDownloader pattern from DocSummarizer.
/// </summary>
public class ClassifierModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _modelDirectory;
    private readonly ILogger<ClassifierModelDownloader> _logger;

    public ClassifierModelDownloader(
        IOptions<DomainClassifierConfig> config,
        ILogger<ClassifierModelDownloader> logger)
    {
        _modelDirectory = config.Value.ModelDirectory;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DomainClassifier/1.0");
    }

    /// <summary>
    ///     Ensure a classifier model is downloaded and return local paths.
    /// </summary>
    public async Task<ClassifierModelPaths> EnsureModelAsync(
        ClassifierModelInfo model,
        CancellationToken ct = default)
    {
        var subDir = model.Task switch
        {
            ModelTask.SequenceClassification => "classifiers",
            ModelTask.TokenClassification => "ner",
            _ => "models"
        };

        var modelDir = Path.Combine(_modelDirectory, subDir, SanitizeName(model.Name));
        Directory.CreateDirectory(modelDir);

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        var tasks = new List<Task>();

        if (!File.Exists(modelPath))
            tasks.Add(DownloadFileAsync(model.GetModelUrl(), modelPath,
                $"Downloading {model.Name} model", model.SizeBytes, ct));

        if (!File.Exists(tokenizerPath))
            tasks.Add(DownloadFileAsync(model.GetTokenizerUrl(), tokenizerPath,
                "Downloading tokenizer", null, ct));

        if (!File.Exists(vocabPath))
            tasks.Add(DownloadFileAsync(model.GetVocabUrl(), vocabPath,
                "Downloading vocab", null, ct));

        if (tasks.Count > 0)
        {
            _logger.LogInformation(
                "First run: downloading ONNX classifier model {ModelName} (~{SizeMB}MB)...",
                model.Name, model.SizeBytes / 1_000_000);
            _logger.LogInformation("Models cached at: {ModelDir}", modelDir);

            await Task.WhenAll(tasks);

            _logger.LogInformation("Model {ModelName} downloaded successfully", model.Name);
        }

        return new ClassifierModelPaths(modelPath, tokenizerPath, vocabPath);
    }

    private async Task DownloadFileAsync(
        string url, string localPath, string description,
        long? expectedSize, CancellationToken ct)
    {
        var tempPath = localPath + ".tmp";

        try
        {
            _logger.LogDebug("{Description} from {Url}", description, url);

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await contentStream.CopyToAsync(fileStream, ct);

            // Move after stream is closed
            fileStream.Close();
            File.Move(tempPath, localPath, true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static string SanitizeName(string name) =>
        name.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
}

/// <summary>
///     Paths to downloaded classifier model files.
/// </summary>
public record ClassifierModelPaths(string ModelPath, string TokenizerPath, string VocabPath);

using System.Collections.Concurrent;
using DomainClassifier.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DomainClassifier.Core.Services.Onnx;

/// <summary>
///     ONNX-based sequence classification. Handles [batch, num_classes] output tensors.
///     Mirrors OnnxEmbeddingService pattern but for classification instead of embeddings.
/// </summary>
public class OnnxClassifierService : IOnnxClassifierService, IDisposable
{
    private readonly ClassifierModelDownloader _downloader;
    private readonly DomainClassifierConfig _config;
    private readonly ILogger<OnnxClassifierService> _logger;

    // Cache sessions per model to avoid reloading
    private readonly ConcurrentDictionary<string, ModelSession> _sessions = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public OnnxClassifierService(
        ClassifierModelDownloader downloader,
        IOptions<DomainClassifierConfig> config,
        ILogger<OnnxClassifierService> logger)
    {
        _downloader = downloader;
        _config = config.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(ClassifierModelInfo model, CancellationToken ct = default)
    {
        if (_sessions.ContainsKey(model.Name))
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_sessions.ContainsKey(model.Name))
                return;

            var paths = await _downloader.EnsureModelAsync(model, ct);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

            var session = new InferenceSession(paths.ModelPath, options);

            HuggingFaceTokenizer? tokenizer = null;
            if (File.Exists(paths.TokenizerPath))
                tokenizer = HuggingFaceTokenizer.FromFile(paths.TokenizerPath);
            else if (File.Exists(paths.VocabPath))
                tokenizer = HuggingFaceTokenizer.FromVocabFile(paths.VocabPath);

            if (tokenizer == null)
                throw new InvalidOperationException(
                    $"No tokenizer found for model {model.Name}");

            _sessions[model.Name] = new ModelSession(session, tokenizer);

            _logger.LogInformation("ONNX classifier loaded: {ModelName} ({LabelCount} labels)",
                model.Name, model.Labels.Length);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<ClassificationResult> ClassifyAsync(
        ClassifierModelInfo model,
        string text,
        CancellationToken ct = default)
    {
        await InitializeAsync(model, ct);

        var modelSession = _sessions[model.Name];
        var maxLen = Math.Min(_config.MaxSequenceLength, model.MaxSequenceLength);

        var (inputIds, attentionMask, tokenTypeIds) = modelSession.Tokenizer.Encode(text, maxLen);

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, tokenTypeIds.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        using var results = modelSession.Session.Run(inputs);

        // Output is [1, num_classes] logits
        var output = results.First(r => r.Name == "logits" || r.Name == "output_0");
        var logits = output.AsTensor<float>();

        return DecodeClassification(logits, model.Labels, 0);
    }

    public async Task<ClassificationResult[]> ClassifyBatchAsync(
        ClassifierModelInfo model,
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var textList = texts.ToList();
        var results = new ClassificationResult[textList.Count];

        // Sequential for simplicity - batch inference can be added later
        for (var i = 0; i < textList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = await ClassifyAsync(model, textList[i], ct);
        }

        return results;
    }

    private static ClassificationResult DecodeClassification(
        Tensor<float> logits, string[] labels, int batchIndex)
    {
        var numClasses = labels.Length;
        var scores = new float[numClasses];

        for (var i = 0; i < numClasses; i++)
            scores[i] = logits[batchIndex, i];

        // Softmax
        var maxScore = scores.Max();
        var expScores = scores.Select(s => MathF.Exp(s - maxScore)).ToArray();
        var sumExp = expScores.Sum();
        var probabilities = expScores.Select(e => e / sumExp).ToArray();

        // Find best label
        var bestIndex = 0;
        for (var i = 1; i < numClasses; i++)
            if (probabilities[i] > probabilities[bestIndex])
                bestIndex = i;

        var allScores = new Dictionary<string, double>();
        for (var i = 0; i < numClasses; i++)
            allScores[labels[i]] = probabilities[i];

        return new ClassificationResult(labels[bestIndex], probabilities[bestIndex], allScores);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Session.Dispose();
        _sessions.Clear();
        _initLock.Dispose();
    }

    private record ModelSession(InferenceSession Session, HuggingFaceTokenizer Tokenizer);
}

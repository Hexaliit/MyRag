namespace DomainClassifier.Core.Services.Onnx;

/// <summary>
///     Registry of available ONNX classifier and NER models with download URLs.
///     Mirrors OnnxModelRegistry pattern from DocSummarizer.
/// </summary>
public static class ClassifierModelRegistry
{
    /// <summary>
    ///     FinancialBERT fine-tuned for sentiment analysis.
    ///     Labels: positive, neutral, negative.
    /// </summary>
    public static ClassifierModelInfo FinancialBertSentiment => new()
    {
        Name = "FinancialBERT-Sentiment",
        HuggingFaceRepo = "ahmedrachid/FinancialBERT-Sentiment-Analysis",
        ModelFile = "onnx/model.onnx",
        TokenizerFile = "tokenizer.json",
        VocabFile = "vocab.txt",
        Labels = ["positive", "neutral", "negative"],
        Task = ModelTask.SequenceClassification,
        MaxSequenceLength = 512,
        SizeBytes = 440_000_000
    };

    /// <summary>
    ///     Build HuggingFace download URL for a model file.
    /// </summary>
    public static string GetDownloadUrl(string repo, string file) =>
        $"https://huggingface.co/{repo}/resolve/main/{file}";
}

/// <summary>
///     Metadata for an ONNX classifier or NER model.
/// </summary>
public class ClassifierModelInfo
{
    public required string Name { get; init; }
    public required string HuggingFaceRepo { get; init; }
    public required string ModelFile { get; init; }
    public required string TokenizerFile { get; init; }
    public required string VocabFile { get; init; }
    public required string[] Labels { get; init; }
    public required ModelTask Task { get; init; }
    public required int MaxSequenceLength { get; init; }
    public long SizeBytes { get; init; }

    public string GetModelUrl() =>
        ClassifierModelRegistry.GetDownloadUrl(HuggingFaceRepo, ModelFile);

    public string GetTokenizerUrl() =>
        ClassifierModelRegistry.GetDownloadUrl(HuggingFaceRepo, TokenizerFile);

    public string GetVocabUrl() =>
        ClassifierModelRegistry.GetDownloadUrl(HuggingFaceRepo, VocabFile);
}

/// <summary>
///     The type of ML task an ONNX model performs.
/// </summary>
public enum ModelTask
{
    /// <summary>
    ///     Sequence classification: [batch, num_classes] logits.
    /// </summary>
    SequenceClassification,

    /// <summary>
    ///     Token classification (NER): [batch, seq_len, num_labels] logits.
    /// </summary>
    TokenClassification,

    /// <summary>
    ///     Feature extraction (embeddings): [batch, seq_len, hidden_size].
    /// </summary>
    FeatureExtraction
}

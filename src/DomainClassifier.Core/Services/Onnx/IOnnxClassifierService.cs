namespace DomainClassifier.Core.Services.Onnx;

/// <summary>
///     ONNX-based sequence classification service.
///     Handles domain detection and sentiment analysis.
/// </summary>
public interface IOnnxClassifierService
{
    /// <summary>
    ///     Initialize the model (downloads if needed).
    /// </summary>
    Task InitializeAsync(ClassifierModelInfo model, CancellationToken ct = default);

    /// <summary>
    ///     Classify text and return label + confidence.
    /// </summary>
    Task<ClassificationResult> ClassifyAsync(
        ClassifierModelInfo model,
        string text,
        CancellationToken ct = default);

    /// <summary>
    ///     Batch classification for multiple texts.
    /// </summary>
    Task<ClassificationResult[]> ClassifyBatchAsync(
        ClassifierModelInfo model,
        IEnumerable<string> texts,
        CancellationToken ct = default);
}

/// <summary>
///     Result of ONNX sequence classification.
/// </summary>
public record ClassificationResult(
    string Label,
    double Confidence,
    Dictionary<string, double> AllScores);

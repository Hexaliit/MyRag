namespace DomainClassifier.Core.Services.Onnx;

/// <summary>
///     ONNX-based token classification (NER) service.
/// </summary>
public interface IOnnxNerService
{
    /// <summary>
    ///     Initialize the NER model (downloads if needed).
    /// </summary>
    Task InitializeAsync(ClassifierModelInfo model, CancellationToken ct = default);

    /// <summary>
    ///     Extract named entities from text.
    /// </summary>
    Task<IReadOnlyList<NerEntity>> ExtractAsync(
        ClassifierModelInfo model,
        string text,
        CancellationToken ct = default);
}

/// <summary>
///     A named entity extracted by token classification.
/// </summary>
public record NerEntity(
    string Text,
    string Label,
    int StartOffset,
    int EndOffset,
    double Confidence);

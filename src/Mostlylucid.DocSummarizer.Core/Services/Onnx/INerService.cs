namespace Mostlylucid.DocSummarizer.Services.Onnx;

/// <summary>
/// Interface for Named Entity Recognition services.
/// Implementations extract people, organizations, locations, and miscellaneous entities from text.
/// </summary>
public interface INerService : IDisposable
{
    /// <summary>
    /// Whether the NER model files are available on disk.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Initialize the ONNX inference session and tokenizer.
    /// Call before <see cref="ExtractEntitiesAsync"/>.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Extract named entities from text.
    /// Returns deduplicated entities with confidence scores.
    /// </summary>
    Task<List<NerEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default);
}

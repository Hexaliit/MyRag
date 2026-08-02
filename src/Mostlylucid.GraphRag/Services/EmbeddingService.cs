using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace Mostlylucid.GraphRag.Services;

/// <summary>
///     ONNX BERT embedding service wrapper.
/// </summary>
public sealed class EmbeddingService : IDisposable, IEmbeddingService
{
    private readonly OnnxEmbeddingService _inner;

    public EmbeddingService()
    {
        var config = new OnnxConfig
        {
            EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2,
            UseQuantized = false,
            ExecutionProvider = OnnxExecutionProvider.Auto
        };
        _inner = new OnnxEmbeddingService(config);
    }

    public int Dimension => _inner.EmbeddingDimension;

    public int EmbeddingDimension => _inner.EmbeddingDimension;

    public void Dispose()
    {
        _inner.Dispose();
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        return _inner.InitializeAsync(ct);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return _inner.EmbedAsync(text, ct);
    }

    public Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        return _inner.EmbedBatchAsync(texts, ct);
    }
}
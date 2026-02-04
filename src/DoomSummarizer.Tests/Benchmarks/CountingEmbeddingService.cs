using System.IO.Hashing;
using System.Text;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.Benchmarks;

/// <summary>
///     Test double for <see cref="IEmbeddingService" /> that returns deterministic,
///     hash-based fake embeddings and counts all embedding calls.
///     Embeddings are L2-normalized so cosine similarity behaves realistically.
/// </summary>
public sealed class CountingEmbeddingService : IEmbeddingService
{
    public CountingEmbeddingService(int dimension = 384)
    {
        EmbeddingDimension = dimension;
    }

    // ── Counters ───────────────────────────────────────────────────────
    public int SingleEmbedCalls { get; private set; }
    public int BatchEmbedCalls { get; private set; }
    public int TotalTextsEmbedded { get; private set; }

    public int EmbeddingDimension { get; }

    // ── IEmbeddingService ──────────────────────────────────────────────

    public Task InitializeAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        SingleEmbedCalls++;
        TotalTextsEmbedded++;
        return Task.FromResult(GenerateEmbedding(text));
    }

    public Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        BatchEmbedCalls++;
        var list = texts.ToList();
        TotalTextsEmbedded += list.Count;
        var result = new float[list.Count][];
        for (var i = 0; i < list.Count; i++)
            result[i] = GenerateEmbedding(list[i]);
        return Task.FromResult(result);
    }

    public void Reset()
    {
        SingleEmbedCalls = 0;
        BatchEmbedCalls = 0;
        TotalTextsEmbedded = 0;
    }

    // ── Deterministic hash-based embedding ─────────────────────────────

    /// <summary>
    ///     Generate a deterministic, L2-normalized embedding from text content.
    ///     Uses XxHash64 seeded with dimension index to fill each float slot,
    ///     then normalizes to unit length so cosine similarity works correctly.
    /// </summary>
    private float[] GenerateEmbedding(string text)
    {
        var vec = new float[EmbeddingDimension];
        var bytes = Encoding.UTF8.GetBytes(text);

        for (var i = 0; i < EmbeddingDimension; i++)
        {
            // Seed each dimension with a different hash of the text
            var seed = (ulong)i * 0x9E3779B97F4A7C15UL;
            var hash = XxHash64.HashToUInt64(bytes, (long)seed);
            // Map to [-1, 1] range
            vec[i] = (float)(hash / (double)ulong.MaxValue) * 2f - 1f;
        }

        // L2-normalize
        var norm = 0f;
        for (var i = 0; i < EmbeddingDimension; i++)
            norm += vec[i] * vec[i];
        norm = MathF.Sqrt(norm);

        if (norm > 0f)
            for (var i = 0; i < EmbeddingDimension; i++)
                vec[i] /= norm;

        return vec;
    }
}
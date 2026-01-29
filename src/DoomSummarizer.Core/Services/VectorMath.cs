using System.Numerics.Tensors;
using OnnxTensor = Microsoft.ML.OnnxRuntime.Tensors.Tensor<float>;

namespace DoomSummarizer.Services;

/// <summary>
/// High-performance vector math using <see cref="TensorPrimitives"/> (System.Numerics.Tensors).
/// Automatically selects the best hardware intrinsics (AVX2, AVX-512, ARM NEON) at runtime.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Computes cosine similarity: dot(a, b) / (||a|| * ||b||).
    /// Uses <see cref="TensorPrimitives.CosineSimilarity"/> for hardware-accelerated computation.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        return TensorPrimitives.CosineSimilarity(a, b);
    }

    /// <summary>
    /// Computes dot product using hardware-accelerated <see cref="TensorPrimitives.Dot"/>.
    /// For L2-normalized vectors, this equals cosine similarity.
    /// </summary>
    public static float DotProduct(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        return TensorPrimitives.Dot(a.AsSpan(), b.AsSpan());
    }

    /// <summary>
    /// Cosine similarity optimized for L2-normalized vectors (just dot product).
    /// Since <see cref="EmbeddingService"/> always L2-normalizes embeddings, this skips
    /// the norm computation entirely.
    /// </summary>
    public static float CosineSimilarityNormalized(float[] a, float[] b)
        => DotProduct(a, b);

    /// <summary>
    /// L2-normalizes a vector in-place using hardware-accelerated operations.
    /// </summary>
    public static void L2Normalize(float[] vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm <= 0) return;
        TensorPrimitives.Divide(vector, norm, vector);
    }

    /// <summary>
    /// Adds a scaled source vector to a target vector in-place: target[i] += source[i] * scale.
    /// Uses <see cref="TensorPrimitives"/> for hardware-accelerated multiply-add.
    /// </summary>
    public static void AddScaled(float[] target, float[] source, float scale)
    {
        var len = Math.Min(target.Length, source.Length);
        var targetSpan = target.AsSpan(0, len);
        var sourceSpan = (ReadOnlySpan<float>)source.AsSpan(0, len);

        // Multiply source by scale into a temp buffer, then add to target
        var temp = len <= 512 ? stackalloc float[len] : new float[len];
        TensorPrimitives.Multiply(sourceSpan, scale, temp);
        TensorPrimitives.Add(targetSpan, (ReadOnlySpan<float>)temp, targetSpan);
    }

    /// <summary>
    /// Hardware-accelerated mean pooling over a transformer hidden state tensor.
    /// Accumulates embeddings for valid attention tokens, then divides by count.
    /// </summary>
    /// <param name="hiddenState">Tensor with shape [1, seqLen, embeddingDim].</param>
    /// <param name="attentionMask">Mask of length seqLen (1 = include, 0 = exclude).</param>
    /// <param name="embeddingDim">Embedding dimension (e.g., 384 for MiniLM).</param>
    public static float[] MeanPool(OnnxTensor hiddenState, long[] attentionMask, int embeddingDim)
    {
        var seqLen = attentionMask.Length;
        var embedding = new float[embeddingDim];
        var validTokens = 0L;

        for (var i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] != 1) continue;
            validTokens++;
        }

        if (validTokens == 0) return embedding;

        // Extract rows and accumulate using TensorPrimitives
        Span<float> row = embeddingDim <= 512 ? stackalloc float[embeddingDim] : new float[embeddingDim];

        for (var i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] != 1) continue;

            // Extract row from hiddenState[0, i, :]
            for (var j = 0; j < embeddingDim; j++)
                row[j] = hiddenState[0, i, j];

            // Accumulate: embedding += row
            TensorPrimitives.Add(embedding, (ReadOnlySpan<float>)row, embedding);
        }

        // Divide by token count to get mean
        TensorPrimitives.Divide(embedding, validTokens, embedding);

        return embedding;
    }
}

using System.Numerics.Tensors;
using OnnxTensor = Microsoft.ML.OnnxRuntime.Tensors.Tensor<float>;

namespace DoomSummarizer.Services;

/// <summary>
///     DoomSummarizer-specific vector math.
///     Core operations (CosineSimilarity, DotProduct, L2Normalize, AddScaled) are in
///     <see cref="Mostlylucid.DocSummarizer.Services.Utilities.VectorMath" />.
///     This class only contains operations that depend on ONNX types.
/// </summary>
public static class VectorMath
{
    // Delegate to shared implementation — these preserve the DoomSummarizer.Services.VectorMath call sites.
    /// <inheritdoc cref="Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.CosineSimilarityFloat" />
    public static float CosineSimilarity(float[] a, float[] b)
    {
        return Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.CosineSimilarityFloat(a, b);
    }

    /// <inheritdoc cref="Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.DotProduct" />
    public static float DotProduct(float[] a, float[] b)
    {
        return Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.DotProduct(a, b);
    }

    /// <inheritdoc cref="Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.CosineSimilarityNormalized" />
    public static float CosineSimilarityNormalized(float[] a, float[] b)
    {
        return Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.CosineSimilarityNormalized(a, b);
    }

    /// <inheritdoc cref="Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.L2Normalize" />
    public static void L2Normalize(float[] vector)
    {
        Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.L2Normalize(vector);
    }

    /// <inheritdoc cref="Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.AddScaled" />
    public static void AddScaled(float[] target, float[] source, float scale)
    {
        Mostlylucid.DocSummarizer.Services.Utilities.VectorMath.AddScaled(target, source, scale);
    }

    /// <summary>
    ///     Hardware-accelerated mean pooling over a transformer hidden state tensor.
    ///     Accumulates embeddings for valid attention tokens, then divides by count.
    ///     This is ONNX-specific and stays in DoomSummarizer.Core.
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

        var row = embeddingDim <= 512 ? stackalloc float[embeddingDim] : new float[embeddingDim];

        for (var i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] != 1) continue;

            for (var j = 0; j < embeddingDim; j++)
                row[j] = hiddenState[0, i, j];

            TensorPrimitives.Add(embedding, row, embedding);
        }

        TensorPrimitives.Divide(embedding, validTokens, embedding);
        return embedding;
    }
}
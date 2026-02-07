using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

namespace Mostlylucid.DocSummarizer.Services.Utilities;

/// <summary>
///     High-performance vector math using <see cref="TensorPrimitives" /> (System.Numerics.Tensors).
///     Automatically selects the best hardware intrinsics (AVX2, AVX-512, ARM NEON) at runtime.
///     Unified implementation shared across DoomSummarizer, DocSummarizer, ImageSummarizer, and LucidRAG.
/// </summary>
public static class VectorMath
{
    /// <summary>
    ///     Compute cosine similarity between two vectors (float precision).
    ///     Uses <see cref="TensorPrimitives.CosineSimilarity" /> for hardware-accelerated computation.
    ///     Returns value between -1 and 1, where 1 = identical direction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        return TensorPrimitives.CosineSimilarity(a, b);
    }

    /// <summary>
    ///     Compute cosine similarity between two vectors (double precision).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CosineSimilarity(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dotProduct = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dotProduct / denom;
    }

    /// <summary>
    ///     Cosine similarity optimized for L2-normalized vectors (just dot product).
    ///     Use when you know both vectors are already unit-length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CosineSimilarityNormalized(float[] a, float[] b)
    {
        return DotProduct(a, b);
    }

    /// <summary>
    ///     Compute dot product using hardware-accelerated <see cref="TensorPrimitives.Dot" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DotProduct(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        return TensorPrimitives.Dot(a.AsSpan(), b.AsSpan());
    }

    /// <summary>
    ///     Compute the L2 norm (magnitude) of a vector using hardware-accelerated <see cref="TensorPrimitives.Norm" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float L2Norm(float[] v)
    {
        if (v.Length == 0) return 0;
        return TensorPrimitives.Norm(v);
    }

    /// <summary>
    ///     L2-normalize a vector in-place using hardware-accelerated operations.
    /// </summary>
    public static void L2Normalize(float[] vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm <= 0) return;
        TensorPrimitives.Divide(vector, norm, vector);
    }

    /// <summary>
    ///     Normalize a vector to unit length in-place.
    ///     Alias for <see cref="L2Normalize" /> for backward compatibility.
    /// </summary>
    public static void NormalizeInPlace(float[] v)
    {
        L2Normalize(v);
    }

    /// <summary>
    ///     Normalize a vector to unit length, returning a new array.
    /// </summary>
    public static float[] Normalize(float[] v)
    {
        var result = new float[v.Length];
        Array.Copy(v, result, v.Length);
        L2Normalize(result);
        return result;
    }

    /// <summary>
    ///     Add a scaled source vector to a target vector in-place: target[i] += source[i] * scale.
    ///     Uses <see cref="TensorPrimitives" /> for hardware-accelerated multiply-add.
    /// </summary>
    public static void AddScaled(float[] target, float[] source, float scale)
    {
        var len = Math.Min(target.Length, source.Length);
        Span<float> targetSpan = target.AsSpan(0, len);
        ReadOnlySpan<float> sourceSpan = source.AsSpan(0, len);

        Span<float> temp = len <= 512 ? stackalloc float[len] : new float[len];
        TensorPrimitives.Multiply(sourceSpan, scale, temp);
        TensorPrimitives.Add(targetSpan, (ReadOnlySpan<float>)temp, targetSpan);
    }

    /// <summary>
    ///     Compute the Euclidean (L2) distance between two vectors.
    /// </summary>
    public static double EuclideanDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return double.MaxValue;

        Span<float> diff = a.Length <= 512 ? stackalloc float[a.Length] : new float[a.Length];
        TensorPrimitives.Subtract((ReadOnlySpan<float>)a, (ReadOnlySpan<float>)b, diff);
        return TensorPrimitives.Norm(diff);
    }

    /// <summary>
    ///     Compute the centroid (average) of multiple vectors.
    /// </summary>
    public static float[] ComputeCentroid(IEnumerable<float[]> vectors)
    {
        var vectorList = vectors.ToList();
        if (vectorList.Count == 0)
            return [];

        var dim = vectorList[0].Length;
        var centroid = new float[dim];

        foreach (var v in vectorList)
            TensorPrimitives.Add(centroid, (ReadOnlySpan<float>)v, centroid);

        TensorPrimitives.Divide(centroid, vectorList.Count, centroid);
        return centroid;
    }

    /// <summary>
    ///     Compute weighted average of vectors.
    /// </summary>
    public static float[] WeightedAverage(IEnumerable<(float[] Vector, double Weight)> weightedVectors)
    {
        var items = weightedVectors.ToList();
        if (items.Count == 0)
            return [];

        var dim = items[0].Vector.Length;
        var result = new float[dim];
        double totalWeight = 0;

        foreach (var (vector, weight) in items)
        {
            AddScaled(result, vector, (float)weight);
            totalWeight += weight;
        }

        if (totalWeight > 0)
            TensorPrimitives.Divide(result, (float)totalWeight, result);

        return result;
    }

    /// <summary>
    ///     Find top-K most similar vectors to query using cosine similarity.
    /// </summary>
    public static List<(int Index, double Similarity)> TopKBySimilarity(
        float[] query,
        IReadOnlyList<float[]> vectors,
        int topK)
    {
        var scores = new List<(int Index, double Similarity)>(vectors.Count);

        for (var i = 0; i < vectors.Count; i++)
        {
            var sim = CosineSimilarity(query, vectors[i]);
            scores.Add((i, sim));
        }

        return scores
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .ToList();
    }
}
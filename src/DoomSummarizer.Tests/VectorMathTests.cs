using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

public class VectorMathTests
{
    private const float Tolerance = 1e-5f;

    /// <summary>
    ///     Reference scalar cosine similarity for validation.
    /// </summary>
    private static float ScalarCosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }

    [Fact]
    public void CosineSimilarity_MatchesScalarReference()
    {
        var rng = new Random(42);
        var a = Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var b = Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();

        var expected = ScalarCosineSimilarity(a, b);
        var actual = VectorMath.CosineSimilarity(a, b);

        Assert.InRange(Math.Abs(expected - actual), 0, Tolerance);
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = Enumerable.Range(0, 384).Select(i => (float)(i + 1)).ToArray();
        var sim = VectorMath.CosineSimilarity(v, v);
        Assert.InRange(sim, 1.0f - Tolerance, 1.0f + Tolerance);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[384];
        var b = new float[384];
        a[0] = 1.0f;
        b[1] = 1.0f;
        var sim = VectorMath.CosineSimilarity(a, b);
        Assert.InRange(sim, -Tolerance, Tolerance);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var a = new float[384];
        var b = new float[256];
        Assert.Equal(0, VectorMath.CosineSimilarity(a, b));
    }

    [Fact]
    public void DotProduct_MatchesManualComputation()
    {
        var a = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var b = new float[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        // 1*10 + 2*9 + 3*8 + 4*7 + 5*6 + 6*5 + 7*4 + 8*3 + 9*2 + 10*1
        // = 10 + 18 + 24 + 28 + 30 + 30 + 28 + 24 + 18 + 10 = 220
        var dot = VectorMath.DotProduct(a, b);
        Assert.InRange(dot, 220 - Tolerance, 220 + Tolerance);
    }

    [Fact]
    public void CosineSimilarityNormalized_MatchesDotProduct()
    {
        var rng = new Random(42);
        var a = Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var b = Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();

        // Normalize both
        VectorMath.L2Normalize(a);
        VectorMath.L2Normalize(b);

        var dot = VectorMath.DotProduct(a, b);
        var cosSim = VectorMath.CosineSimilarityNormalized(a, b);
        var fullCosSim = VectorMath.CosineSimilarity(a, b);

        Assert.InRange(Math.Abs(dot - cosSim), 0, Tolerance);
        Assert.InRange(Math.Abs(dot - fullCosSim), 0, Tolerance);
    }

    [Fact]
    public void L2Normalize_ProducesUnitVector()
    {
        var v = new[] { 3.0f, 4.0f }; // norm = 5
        VectorMath.L2Normalize(v);

        Assert.InRange(v[0], 0.6f - Tolerance, 0.6f + Tolerance);
        Assert.InRange(v[1], 0.8f - Tolerance, 0.8f + Tolerance);

        // Verify unit length
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        Assert.InRange(norm, 1.0f - Tolerance, 1.0f + Tolerance);
    }

    [Fact]
    public void L2Normalize_ZeroVector_RemainsZero()
    {
        var v = new float[384];
        VectorMath.L2Normalize(v);
        Assert.All(v, x => Assert.Equal(0, x));
    }

    [Fact]
    public void L2Normalize_LargeVector_ProducesUnitLength()
    {
        var rng = new Random(42);
        var v = Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 10 - 5)).ToArray();
        VectorMath.L2Normalize(v);

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        Assert.InRange(norm, 1.0f - Tolerance, 1.0f + Tolerance);
    }

    [Fact]
    public void AddScaled_CorrectAccumulation()
    {
        var target = new float[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var source = new float[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        VectorMath.AddScaled(target, source, 0.5f);

        Assert.InRange(target[0], 6.0f - Tolerance, 6.0f + Tolerance); // 1 + 10*0.5
        Assert.InRange(target[7], 48.0f - Tolerance, 48.0f + Tolerance); // 8 + 80*0.5
    }

    [Fact]
    public void CosineSimilarity_NonAlignedLength_MatchesScalar()
    {
        // Test with length not divisible by SIMD register size (tests scalar tail)
        var rng = new Random(42);
        var a = Enumerable.Range(0, 387).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var b = Enumerable.Range(0, 387).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();

        var expected = ScalarCosineSimilarity(a, b);
        var actual = VectorMath.CosineSimilarity(a, b);

        Assert.InRange(Math.Abs(expected - actual), 0, Tolerance);
    }

    [Fact]
    public void CosineSimilarity_SmallVector_MatchesScalar()
    {
        // Very small vector — all scalar tail, no SIMD
        var a = new[] { 1.0f, 2.0f, 3.0f };
        var b = new[] { 4.0f, 5.0f, 6.0f };

        var expected = ScalarCosineSimilarity(a, b);
        var actual = VectorMath.CosineSimilarity(a, b);

        Assert.InRange(Math.Abs(expected - actual), 0, Tolerance);
    }
}
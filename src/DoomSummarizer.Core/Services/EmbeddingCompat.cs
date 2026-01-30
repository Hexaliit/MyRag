namespace DoomSummarizer.Services;

/// <summary>
/// Byte-conversion helpers for embedding storage.
/// Remaining utility after migrating to IEmbeddingService from DocSummarizer.Core.
/// CosineSimilarity calls now go through VectorMath (shared, SIMD-accelerated).
/// </summary>
internal static class EmbeddingCompat
{
    public static float[] FromBytes(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public static byte[] ToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

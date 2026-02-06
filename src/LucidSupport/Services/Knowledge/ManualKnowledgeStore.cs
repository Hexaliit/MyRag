using System.Text.Json;
using DoomSummarizer.Services;

namespace LucidSupport.Services.Knowledge;

/// <summary>
///     In-memory vector store for manual knowledge chunks.
///     Uses brute-force cosine similarity for small corpora.
/// </summary>
public sealed class ManualKnowledgeStore
{
    private readonly List<ManualChunk> _chunks = [];
    private readonly object _lock = new();

    /// <summary>Number of loaded chunks.</summary>
    public int Count
    {
        get { lock (_lock) return _chunks.Count; }
    }

    /// <summary>Add chunks to the store.</summary>
    public void AddChunks(IEnumerable<ManualChunk> chunks)
    {
        lock (_lock)
        {
            _chunks.AddRange(chunks);
        }
    }

    /// <summary>Search for the most similar chunks to a query embedding.</summary>
    public ManualChunk[] Search(float[] queryEmbedding, int topK = 3, float minSimilarity = 0.3f)
    {
        lock (_lock)
        {
            if (_chunks.Count == 0) return [];

            return _chunks
                .Select(c => (Chunk: c, Score: (float)VectorMath.CosineSimilarity(queryEmbedding, c.Embedding)))
                .Where(x => x.Score >= minSimilarity)
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => x.Chunk)
                .ToArray();
        }
    }

    /// <summary>Load chunks from a serialized knowledge.json file.</summary>
    public void LoadFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var chunks = JsonSerializer.Deserialize<List<ManualChunk>>(json, JsonOptions);
        if (chunks is not null)
            AddChunks(chunks);
    }

    /// <summary>Save chunks to a knowledge.json file.</summary>
    public static void SaveToFile(IEnumerable<ManualChunk> chunks, string filePath)
    {
        var json = JsonSerializer.Serialize(chunks.ToList(), JsonOptions);
        File.WriteAllText(filePath, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

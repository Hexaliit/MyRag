namespace Mostlylucid.RAG.Config;

public enum VectorStoreBackend
{
    SqliteVec
}

public class SemanticSearchConfig : IConfigSection
{
    public bool Enabled { get; set; } = true;
    public VectorStoreBackend Backend { get; set; } = VectorStoreBackend.SqliteVec;
    public string CollectionName { get; set; } = "blog_posts";
    public string EmbeddingModelPath { get; set; } = "models/all-MiniLM-L6-v2.onnx";
    public string VocabPath { get; set; } = "models/vocab.txt";
    public int VectorSize { get; set; } = 384;
    public int RelatedPostsCount { get; set; } = 5;
    public float MinimumSimilarityScore { get; set; } = 0.5f;
    public int SearchResultsCount { get; set; } = 10;
    public static string Section => "SemanticSearch";
}

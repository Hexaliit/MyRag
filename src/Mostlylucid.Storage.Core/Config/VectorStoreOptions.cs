using Mostlylucid.Storage.Core.Abstractions;

namespace Mostlylucid.Storage.Core.Config;

public class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    public VectorStoreBackend Backend { get; set; } = VectorStoreBackend.SqliteVec;
    public string CollectionName { get; set; } = "default";
    public bool PersistVectors { get; set; } = true;
    public bool ReuseExistingEmbeddings { get; set; } = true;
    public bool ReindexOnStartup { get; set; }
    public SqliteVecOptions SqliteVec { get; set; } = new();
    public InMemoryOptions InMemory { get; set; } = new();

    public static VectorStoreOptions ForToolMode()
    {
        return new VectorStoreOptions
        {
            Backend = VectorStoreBackend.InMemory,
            PersistVectors = false,
            ReuseExistingEmbeddings = false,
            ReindexOnStartup = false,
            CollectionName = "tool_temp"
        };
    }

    public static VectorStoreOptions ForStandaloneMode(string dataDirectory = "./data")
    {
        return new VectorStoreOptions
        {
            Backend = VectorStoreBackend.SqliteVec,
            PersistVectors = true,
            ReuseExistingEmbeddings = true,
            ReindexOnStartup = false,
            CollectionName = "documents",
            SqliteVec = new SqliteVecOptions
            {
                DatabasePath = Path.Combine(dataDirectory, "rag.db")
            }
        };
    }

    public static VectorStoreOptions ForProductionMode(string dataDirectory = "./data")
    {
        return new VectorStoreOptions
        {
            Backend = VectorStoreBackend.SqliteVec,
            PersistVectors = true,
            ReuseExistingEmbeddings = true,
            ReindexOnStartup = false,
            CollectionName = "documents",
            SqliteVec = new SqliteVecOptions
            {
                DatabasePath = Path.Combine(dataDirectory, "rag.db")
            }
        };
    }
}

public class SqliteVecOptions
{
    public string DatabasePath { get; set; } = "./data/rag.db";
}

public class InMemoryOptions
{
    public int MaxDocuments { get; set; } = 0;
    public bool Verbose { get; set; } = false;
}

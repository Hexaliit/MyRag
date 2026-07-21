using Mostlylucid.Storage.Core.Abstractions.Models;

namespace Mostlylucid.Storage.Core.Abstractions;

public interface IVectorStore : IDisposable
{
    bool IsPersistent { get; }
    VectorStoreBackend Backend { get; }

    Task CreateCollectionAsync(string collectionName, int vectorDimensions, CancellationToken ct = default);
    Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default);
    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default);

    Task UpsertAsync(string collectionName, VectorStoreRecord record, CancellationToken ct = default);
    Task UpsertBatchAsync(string collectionName, IEnumerable<VectorStoreRecord> records, CancellationToken ct = default);
    Task DeleteAsync(string collectionName, string documentId, CancellationToken ct = default);

    Task<VectorStoreRecord?> GetByIdAsync(string collectionName, string documentId, CancellationToken ct = default);
    Task<long> CountAsync(string collectionName, CancellationToken ct = default);
    Task<List<VectorStoreRecord>> GetAllAsync(string collectionName, string? parentId = null, CancellationToken ct = default);

    Task<List<SearchResult>> SearchAsync(string collectionName, float[] queryVector, SearchFilter? filter = null, CancellationToken ct = default);
    Task<List<SearchResult>> HybridSearchAsync(string collectionName, string queryText, float[] queryVector, SearchFilter? filter = null, CancellationToken ct = default);

    Task<Dictionary<string, VectorStoreRecord>> GetByHashAsync(string collectionName, IEnumerable<string> contentHashes, CancellationToken ct = default);
    Task RemoveStaleAsync(string collectionName, string parentId, IEnumerable<string> validContentHashes, CancellationToken ct = default);
}

public enum VectorStoreBackend
{
    InMemory,
    SqliteVec
}

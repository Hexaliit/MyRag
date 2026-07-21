using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;
using Mostlylucid.Storage.Core.Config;

namespace Mostlylucid.Storage.Core.Implementations;

public class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, List<VectorStoreRecord>> _collections = new();
    private readonly ConcurrentDictionary<string, int> _dimensions = new();
    private readonly ILogger<InMemoryVectorStore> _logger;
    private readonly InMemoryOptions _options;
    private bool _disposed;

    public InMemoryVectorStore(IOptions<VectorStoreOptions> options, ILogger<InMemoryVectorStore> logger)
    {
        _logger = logger;
        _options = options.Value.InMemory;
    }

    public bool IsPersistent => false;
    public VectorStoreBackend Backend => VectorStoreBackend.InMemory;

    public void Dispose()
    {
        if (_disposed) return;
        _collections.Clear();
        _disposed = true;
    }

    public Task CreateCollectionAsync(string collectionName, int vectorDimensions, CancellationToken ct = default)
    {
        _collections.TryAdd(collectionName, new List<VectorStoreRecord>());
        _dimensions[collectionName] = vectorDimensions;
        if (_options.Verbose)
            _logger.LogInformation("Created in-memory collection {Collection} (dim={Dim})", collectionName, vectorDimensions);
        return Task.CompletedTask;
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        _collections.TryRemove(collectionName, out _);
        _dimensions.TryRemove(collectionName, out _);
        return Task.CompletedTask;
    }

    public Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        return Task.FromResult(_collections.ContainsKey(collectionName));
    }

    public Task UpsertAsync(string collectionName, VectorStoreRecord record, CancellationToken ct = default)
    {
        return UpsertBatchAsync(collectionName, [record], ct);
    }

    public Task UpsertBatchAsync(string collectionName, IEnumerable<VectorStoreRecord> records, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            throw new InvalidOperationException($"Collection '{collectionName}' not found.");

        var list = records.ToList();
        lock (collection)
        {
            var newIds = list.Select(r => r.Id).ToHashSet();
            collection.RemoveAll(r => newIds.Contains(r.Id));
            collection.AddRange(list);
        }

        if (_options.Verbose)
            _logger.LogDebug("Upserted {Count} records to in-memory collection {Collection}", list.Count, collectionName);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        if (_collections.TryGetValue(collectionName, out var collection))
        {
            lock (collection)
            {
                collection.RemoveAll(r => r.Id == documentId);
            }
        }
        return Task.CompletedTask;
    }

    public Task<VectorStoreRecord?> GetByIdAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult<VectorStoreRecord?>(null);
        var record = collection.FirstOrDefault(r => r.Id == documentId);
        return Task.FromResult(record);
    }

    public Task<long> CountAsync(string collectionName, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(0L);
        return Task.FromResult((long)collection.Count);
    }

    public Task<List<VectorStoreRecord>> GetAllAsync(string collectionName, string? parentId = null, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(new List<VectorStoreRecord>());
        var results = parentId == null
            ? collection.ToList()
            : collection.Where(r => r.ParentId == parentId).ToList();
        return Task.FromResult(results);
    }

    public Task<List<SearchResult>> SearchAsync(string collectionName, float[] queryVector, SearchFilter? filter = null, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(new List<SearchResult>());

        var topK = filter?.TopK ?? 10;
        var candidates = collection.AsEnumerable();

        if (filter?.Namespace != null) candidates = candidates.Where(r => r.Namespace == filter.Namespace);
        if (filter?.DocumentId != null) candidates = candidates.Where(r => r.DocumentId == filter.DocumentId);
        if (filter?.Language != null) candidates = candidates.Where(r => r.Language == filter.Language);
        if (filter?.SourceFile != null) candidates = candidates.Where(r => r.SourceFile == filter.SourceFile);
        if (filter?.MetadataFilter != null && filter.MetadataFilter.Count > 0)
        {
            candidates = candidates.Where(r =>
                filter.MetadataFilter.All(kvp =>
                    r.Metadata.TryGetValue(kvp.Key, out var val) && val.ToString() == kvp.Value));
        }

        var results = candidates
            .Select(r =>
            {
                var score = CosineSimilarity(queryVector, r.Embedding);
                return new { Record = r, Score = score };
            })
            .Where(x => x.Score >= (filter?.MinScore ?? 0))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new SearchResult
            {
                Id = x.Record.Id,
                Score = x.Score,
                CosineScore = x.Score,
                Record = x.Record,
                Metadata = x.Record.Metadata,
                Text = x.Record.Text
            })
            .ToList();

        return Task.FromResult(results);
    }

    public Task<List<SearchResult>> HybridSearchAsync(string collectionName, string queryText, float[] queryVector, SearchFilter? filter = null, CancellationToken ct = default)
    {
        return SearchAsync(collectionName, queryVector, filter, ct);
    }

    public Task<Dictionary<string, VectorStoreRecord>> GetByHashAsync(string collectionName, IEnumerable<string> contentHashes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, VectorStoreRecord>();
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(result);

        var hashSet = contentHashes.ToHashSet();
        foreach (var record in collection)
            if (record.ContentHash != null && hashSet.Contains(record.ContentHash))
                result[record.ContentHash] = record;

        return Task.FromResult(result);
    }

    public Task RemoveStaleAsync(string collectionName, string parentId, IEnumerable<string> validContentHashes, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.CompletedTask;

        var validSet = validContentHashes.ToHashSet();
        lock (collection)
        {
            collection.RemoveAll(r =>
                r.ParentId == parentId && (r.ContentHash == null || !validSet.Contains(r.ContentHash)));
        }
        return Task.CompletedTask;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }
}

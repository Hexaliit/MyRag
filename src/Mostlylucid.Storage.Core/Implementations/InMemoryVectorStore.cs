using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;
using Mostlylucid.Storage.Core.Config;

namespace Mostlylucid.Storage.Core.Implementations;

/// <summary>
///     In-memory vector store with no persistence.
///     Fast and simple - ideal for testing, tool/MCP mode, and one-shot analysis.
///     All data is lost when the process exits.
/// </summary>
public class InMemoryVectorStore : IMultiVectorStore
{
    private readonly ConcurrentDictionary<string, CollectionData> _collections = new();
    private readonly ILogger<InMemoryVectorStore> _logger;
    private readonly InMemoryOptions _options;
    private readonly ConcurrentDictionary<string, CachedSummary> _summaryCache = new();
    private bool _disposed;

    public InMemoryVectorStore(IOptions<VectorStoreOptions> options, ILogger<InMemoryVectorStore> logger)
    {
        _logger = logger;
        _options = options.Value.InMemory;
    }

    public bool IsPersistent => false;
    public VectorStoreBackend Backend => VectorStoreBackend.InMemory;

    // ========== Collection Management ==========

    public Task InitializeAsync(string collectionName, VectorStoreSchema schema, CancellationToken ct = default)
    {
        _collections.TryAdd(collectionName, new CollectionData
        {
            Name = collectionName,
            Schema = schema,
            Documents = new List<VectorDocument>()
        });

        if (_options.Verbose)
            _logger.LogInformation("Initialized in-memory collection {Collection} (dim={Dim})",
                collectionName, schema.VectorDimension);

        return Task.CompletedTask;
    }

    public Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        return Task.FromResult(_collections.ContainsKey(collectionName));
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        _collections.TryRemove(collectionName, out _);

        if (_options.Verbose) _logger.LogInformation("Deleted in-memory collection {Collection}", collectionName);

        return Task.CompletedTask;
    }

    public Task<CollectionStats> GetCollectionStatsAsync(string collectionName, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(new CollectionStats
            {
                CollectionName = collectionName,
                DocumentCount = 0,
                VectorDimension = 0,
                SizeBytes = null
            });

        var sizeBytes = collection.Documents.Sum(d =>
            d.Embedding.Length * sizeof(float) +
            (d.Text?.Length ?? 0) * 2); // Rough estimate

        return Task.FromResult(new CollectionStats
        {
            CollectionName = collectionName,
            DocumentCount = collection.Documents.Count,
            VectorDimension = collection.Schema.VectorDimension,
            SizeBytes = sizeBytes
        });
    }

    // ========== Document Operations ==========

    public Task<bool> HasDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(false);

        var exists = collection.Documents.Any(d => d.Id == documentId);
        return Task.FromResult(exists);
    }

    public Task UpsertDocumentsAsync(string collectionName, IEnumerable<VectorDocument> documents,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            throw new InvalidOperationException(
                $"Collection '{collectionName}' does not exist. Call InitializeAsync first.");

        var docList = documents.ToList();

        // Validate dimensions
        foreach (var doc in docList)
            if (doc.Embedding.Length != collection.Schema.VectorDimension)
                throw new ArgumentException(
                    $"Document {doc.Id} embedding dimension {doc.Embedding.Length} does not match collection dimension {collection.Schema.VectorDimension}");

        lock (collection.Documents)
        {
            // Remove existing documents with same IDs
            var newIds = docList.Select(d => d.Id).ToHashSet();
            collection.Documents.RemoveAll(d => newIds.Contains(d.Id));

            // Add new documents
            collection.Documents.AddRange(docList);

            // Apply max documents limit if configured
            if (_options.MaxDocuments > 0 && collection.Documents.Count > _options.MaxDocuments)
            {
                // LRU eviction - remove oldest documents
                var toRemove = collection.Documents.Count - _options.MaxDocuments;
                collection.Documents.RemoveRange(0, toRemove);

                _logger.LogWarning(
                    "In-memory collection {Collection} exceeded limit ({Limit}), evicted {Count} oldest documents",
                    collectionName, _options.MaxDocuments, toRemove);
            }
        }

        if (_options.Verbose)
            _logger.LogDebug("Upserted {Count} documents to in-memory collection {Collection}",
                docList.Count, collectionName);

        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.CompletedTask;

        lock (collection.Documents)
        {
            collection.Documents.RemoveAll(d => d.Id == documentId);
        }

        return Task.CompletedTask;
    }

    public Task<VectorDocument?> GetDocumentAsync(string collectionName, string documentId,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult<VectorDocument?>(null);

        var doc = collection.Documents.FirstOrDefault(d => d.Id == documentId);
        return Task.FromResult(doc);
    }

    public Task<List<VectorDocument>> GetAllDocumentsAsync(string collectionName, string? parentId = null,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(new List<VectorDocument>());

        var docs = parentId == null
            ? collection.Documents.ToList()
            : collection.Documents.Where(d => d.ParentId == parentId).ToList();

        return Task.FromResult(docs);
    }

    // ========== Search Operations ==========

    public Task<List<VectorSearchResult>> SearchAsync(string collectionName, VectorSearchQuery query,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(new List<VectorSearchResult>());

        var candidates = collection.Documents.AsEnumerable();

        // Filter by parent ID if specified
        if (query.ParentId != null) candidates = candidates.Where(d => d.ParentId == query.ParentId);

        // Apply metadata filters if specified
        if (query.Filters != null && query.Filters.Count > 0)
            candidates = candidates.Where(d =>
            {
                foreach (var filter in query.Filters)
                {
                    if (!d.Metadata.TryGetValue(filter.Key, out var value))
                        return false;

                    if (!value.Equals(filter.Value))
                        return false;
                }

                return true;
            });

        // Score by cosine similarity and return top K
        var results = candidates
            .Select(d =>
            {
                var similarity = CosineSimilarity(query.QueryEmbedding, d.Embedding);
                var distance = 1.0 - similarity;

                return new VectorSearchResult
                {
                    Id = d.Id,
                    Score = similarity,
                    Distance = distance,
                    Document = query.IncludeDocument ? d : null,
                    Metadata = d.Metadata,
                    Text = query.IncludeDocument ? d.Text : null,
                    ParentId = d.ParentId
                };
            })
            .Where(r => r.Score >= query.MinScore && (!query.MaxScore.HasValue || r.Score <= query.MaxScore.Value))
            .OrderByDescending(r => r.Score)
            .Take(query.TopK)
            .ToList();

        return Task.FromResult(results);
    }

    public async Task<List<VectorSearchResult>> FindSimilarAsync(string collectionName, string documentId,
        int topK = 10, CancellationToken ct = default)
    {
        var doc = await GetDocumentAsync(collectionName, documentId, ct);
        if (doc == null) return new List<VectorSearchResult>();

        var query = new VectorSearchQuery
        {
            QueryEmbedding = doc.Embedding,
            TopK = topK + 1, // +1 to exclude self
            IncludeDocument = false
        };

        var results = await SearchAsync(collectionName, query, ct);

        // Remove the document itself from results
        return results.Where(r => r.Id != documentId).Take(topK).ToList();
    }

    // ========== Content Hash-Based Caching ==========

    public Task<Dictionary<string, VectorDocument>> GetDocumentsByHashAsync(string collectionName,
        IEnumerable<string> contentHashes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, VectorDocument>();

        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(result);

        var hashSet = contentHashes.ToHashSet();
        foreach (var doc in collection.Documents)
            if (doc.ContentHash != null && hashSet.Contains(doc.ContentHash))
                result[doc.ContentHash] = doc;

        if (_options.Verbose && result.Count > 0)
            _logger.LogDebug("Found {Found}/{Total} cached documents by hash in collection {Collection}",
                result.Count, hashSet.Count, collectionName);

        return Task.FromResult(result);
    }

    public Task RemoveStaleDocumentsAsync(string collectionName, string parentId, IEnumerable<string> validHashes,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.CompletedTask;

        var validHashSet = validHashes.ToHashSet();

        lock (collection.Documents)
        {
            var countBefore = collection.Documents.Count;

            // Keep documents that don't belong to this parent, or have valid content hashes
            collection.Documents.RemoveAll(d =>
                d.ParentId == parentId &&
                (d.ContentHash == null || !validHashSet.Contains(d.ContentHash)));

            var removed = countBefore - collection.Documents.Count;

            if (_options.Verbose && removed > 0)
                _logger.LogDebug("Removed {Count} stale documents from collection {Collection}",
                    removed, collectionName);
        }

        return Task.CompletedTask;
    }

    // ========== Summary Caching ==========

    public Task<CachedSummary?> GetCachedSummaryAsync(string collectionName, string documentId,
        CancellationToken ct = default)
    {
        var fullKey = $"{collectionName}:{documentId}";
        _summaryCache.TryGetValue(fullKey, out var summary);

        if (_options.Verbose && summary != null)
            _logger.LogDebug("Summary cache hit for document {DocumentId} in collection {Collection}",
                documentId, collectionName);

        return Task.FromResult(summary);
    }

    public Task CacheSummaryAsync(string collectionName, CachedSummary summary, CancellationToken ct = default)
    {
        var fullKey = $"{collectionName}:{summary.DocumentId}";
        _summaryCache[fullKey] = summary;

        if (_options.Verbose)
            _logger.LogDebug("Cached summary for document {DocumentId} in collection {Collection}",
                summary.DocumentId, collectionName);

        return Task.CompletedTask;
    }

    // ========== Multi-Vector Operations ==========

    public Task InitializeMultiVectorAsync(
        string collectionName,
        VectorStoreSchema primarySchema,
        IEnumerable<NamedVectorConfig> namedVectors,
        CancellationToken ct = default)
    {
        // Store named vector configs on the schema so CollectionData has them
        primarySchema.NamedVectors = namedVectors.ToList();

        _collections.TryAdd(collectionName, new CollectionData
        {
            Name = collectionName,
            Schema = primarySchema,
            Documents = new List<VectorDocument>()
        });

        if (_options.Verbose)
        {
            var names = string.Join(", ", primarySchema.NamedVectors.Select(n => $"{n.Name}({n.Dimension})"));
            _logger.LogInformation(
                "Initialized in-memory multi-vector collection {Collection} (primary dim={Dim}, named=[{Named}])",
                collectionName, primarySchema.VectorDimension, names);
        }

        return Task.CompletedTask;
    }

    public Task UpsertMultiVectorDocumentsAsync(
        string collectionName,
        IEnumerable<MultiVectorDocument> documents,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            throw new InvalidOperationException(
                $"Collection '{collectionName}' does not exist. Call InitializeMultiVectorAsync first.");

        var docList = documents.ToList();
        var namedConfigs = collection.Schema.NamedVectors.ToDictionary(n => n.Name);

        foreach (var doc in docList)
        {
            // Validate primary embedding dimension
            if (doc.Embedding.Length != collection.Schema.VectorDimension)
                throw new ArgumentException(
                    $"Document {doc.Id} primary embedding dimension {doc.Embedding.Length} does not match collection dimension {collection.Schema.VectorDimension}");

            // Validate named vector dimensions
            foreach (var (name, vector) in doc.NamedVectors)
            {
                if (!namedConfigs.TryGetValue(name, out var config))
                    throw new ArgumentException(
                        $"Document {doc.Id} has named vector '{name}' which is not configured on collection '{collectionName}'");

                if (vector.Length != config.Dimension)
                    throw new ArgumentException(
                        $"Document {doc.Id} named vector '{name}' dimension {vector.Length} does not match configured dimension {config.Dimension}");
            }
        }

        lock (collection.Documents)
        {
            var newIds = docList.Select(d => d.Id).ToHashSet();
            collection.Documents.RemoveAll(d => newIds.Contains(d.Id));
            collection.Documents.AddRange(docList);

            if (_options.MaxDocuments > 0 && collection.Documents.Count > _options.MaxDocuments)
            {
                var toRemove = collection.Documents.Count - _options.MaxDocuments;
                collection.Documents.RemoveRange(0, toRemove);
            }
        }

        if (_options.Verbose)
            _logger.LogDebug("Upserted {Count} multi-vector documents to in-memory collection {Collection}",
                docList.Count, collectionName);

        return Task.CompletedTask;
    }

    public Task<List<VectorSearchResult>> SearchByNamedVectorAsync(
        string collectionName,
        string vectorName,
        VectorSearchQuery query,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return Task.FromResult(new List<VectorSearchResult>());

        var namedConfig = collection.Schema.NamedVectors.FirstOrDefault(n => n.Name == vectorName);
        if (namedConfig == null)
            throw new ArgumentException(
                $"Named vector '{vectorName}' is not configured on collection '{collectionName}'");

        if (query.QueryEmbedding.Length != namedConfig.Dimension)
            throw new ArgumentException(
                $"Query embedding dimension {query.QueryEmbedding.Length} does not match named vector '{vectorName}' dimension {namedConfig.Dimension}");

        var candidates = collection.Documents.AsEnumerable();

        if (query.ParentId != null) candidates = candidates.Where(d => d.ParentId == query.ParentId);

        var results = candidates
            .OfType<MultiVectorDocument>()
            .Where(d => d.NamedVectors.ContainsKey(vectorName))
            .Select(d =>
            {
                var similarity = CosineSimilarity(query.QueryEmbedding, d.NamedVectors[vectorName]);
                return new VectorSearchResult
                {
                    Id = d.Id,
                    Score = similarity,
                    Distance = 1.0 - similarity,
                    Document = query.IncludeDocument ? d : null,
                    Metadata = d.Metadata,
                    Text = query.IncludeDocument ? d.Text : null,
                    ParentId = d.ParentId
                };
            })
            .Where(r => r.Score >= query.MinScore && (!query.MaxScore.HasValue || r.Score <= query.MaxScore.Value))
            .OrderByDescending(r => r.Score)
            .Take(query.TopK)
            .ToList();

        return Task.FromResult(results);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _collections.Clear();
        _summaryCache.Clear();
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ========== Private Helpers ==========

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

    private class CollectionData
    {
        public required string Name { get; init; }
        public required VectorStoreSchema Schema { get; init; }
        public required List<VectorDocument> Documents { get; init; }
    }
}
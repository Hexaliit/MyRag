using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Config;
using Mostlylucid.RAG.Models;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;

namespace Mostlylucid.RAG.Services;

public class SqliteVecVectorStoreService : IVectorStoreService
{
    private readonly IVectorStore _store;
    private readonly SemanticSearchConfig _config;
    private readonly ILogger<SqliteVecVectorStoreService> _logger;
    private bool _collectionInitialized;

    public SqliteVecVectorStoreService(
        ILogger<SqliteVecVectorStoreService> logger,
        SemanticSearchConfig config,
        IVectorStore store)
    {
        _logger = logger;
        _config = config;
        _store = store;
    }

    public async Task InitializeCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || _collectionInitialized)
            return;

        try
        {
            var exists = await _store.CollectionExistsAsync(_config.CollectionName, cancellationToken);
            if (!exists)
            {
                _logger.LogInformation("Creating collection {CollectionName}", _config.CollectionName);
                await _store.CreateCollectionAsync(_config.CollectionName, _config.VectorSize, cancellationToken);
            }
            _collectionInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize collection {CollectionName}", _config.CollectionName);
            throw;
        }
    }

    public async Task IndexDocumentAsync(BlogPostDocument document, float[] embedding,
        CancellationToken cancellationToken = default)
    {
        await InitializeCollectionAsync(cancellationToken);

        var record = new VectorStoreRecord
        {
            Id = document.Slug,
            DocumentId = document.Slug,
            ChunkId = document.Slug,
            Embedding = embedding,
            Text = JsonSerializer.Serialize(new { document.Title, document.Content }),
            SourceFile = document.Slug,
            Namespace = "blog",
            ContentHash = document.ContentHash
        };

        if (document.Languages.Length > 0)
            record.Metadata["languages"] = string.Join(",", document.Languages);
        if (document.Categories.Length > 0)
            record.Metadata["categories"] = string.Join(",", document.Categories);

        await _store.UpsertAsync(_config.CollectionName, record, cancellationToken);
    }

    public async Task IndexDocumentsAsync(IEnumerable<(BlogPostDocument Document, float[] Embedding)> documents,
        CancellationToken cancellationToken = default)
    {
        await InitializeCollectionAsync(cancellationToken);

        var records = documents.Select(d =>
        {
            var record = new VectorStoreRecord
            {
                Id = d.Document.Slug,
                DocumentId = d.Document.Slug,
                ChunkId = d.Document.Slug,
                Embedding = d.Embedding,
                Text = JsonSerializer.Serialize(new { d.Document.Title, d.Document.Content }),
                SourceFile = d.Document.Slug,
                Namespace = "blog",
                ContentHash = d.Document.ContentHash
            };

            if (d.Document.Languages.Length > 0)
                record.Metadata["languages"] = string.Join(",", d.Document.Languages);
            if (d.Document.Categories.Length > 0)
                record.Metadata["categories"] = string.Join(",", d.Document.Categories);

            return record;
        }).ToList();

        await _store.UpsertBatchAsync(_config.CollectionName, records, cancellationToken);
        _logger.LogInformation("Indexed {Count} blog posts", records.Count);
    }

    public async Task<List<Models.SearchResult>> SearchAsync(float[] queryEmbedding, int limit = 10,
        float scoreThreshold = 0.5f, CancellationToken cancellationToken = default)
    {
        await InitializeCollectionAsync(cancellationToken);

        var filter = new SearchFilter
        {
            TopK = limit,
            MinScore = scoreThreshold,
            Namespace = "blog"
        };

        var results = await _store.SearchAsync(_config.CollectionName, queryEmbedding, filter, cancellationToken);

        return results.Select(r => new Models.SearchResult
        {
            Slug = r.Id,
            Score = (float)r.Score
        }).ToList();
    }

    public async Task<List<Models.SearchResult>> FindRelatedPostsAsync(string slug, int limit = 5,
        CancellationToken cancellationToken = default)
    {
        await InitializeCollectionAsync(cancellationToken);

        var record = await _store.GetByIdAsync(_config.CollectionName, slug, cancellationToken);
        if (record == null)
        {
            _logger.LogWarning("Post {Slug} not found for related search", slug);
            return new List<Models.SearchResult>();
        }

        var filter = new SearchFilter
        {
            TopK = limit + 1,
            Namespace = "blog"
        };

        var results = await _store.SearchAsync(_config.CollectionName, record.Embedding, filter, cancellationToken);

        return results
            .Where(r => r.Id != slug)
            .Take(limit)
            .Select(r => new Models.SearchResult
            {
                Slug = r.Id,
                Score = (float)r.Score
            }).ToList();
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        await _store.DeleteAsync(_config.CollectionName, id, cancellationToken);
    }

    public async Task<string?> GetDocumentHashAsync(string id, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(_config.CollectionName, id, cancellationToken);
        return record?.ContentHash;
    }

    public async Task UpdateLanguagesAsync(string slug, string[] languages, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(_config.CollectionName, slug, cancellationToken);
        if (record == null)
        {
            _logger.LogWarning("Post {Slug} not found for language update", slug);
            return;
        }

        record.Metadata["languages"] = string.Join(",", languages);
        record.UpdatedAt = DateTime.UtcNow;
        await _store.UpsertAsync(_config.CollectionName, record, cancellationToken);
    }

    public async Task AddLanguageAsync(string slug, string language, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(_config.CollectionName, slug, cancellationToken);
        if (record == null)
        {
            _logger.LogWarning("Post {Slug} not found for language add", slug);
            return;
        }

        var existingLanguages = record.Metadata.TryGetValue("languages", out var langs)
            ? langs.ToString()?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string>()
            : new List<string>();

        if (!existingLanguages.Contains(language))
        {
            existingLanguages.Add(language);
            record.Metadata["languages"] = string.Join(",", existingLanguages);
            record.UpdatedAt = DateTime.UtcNow;
            await _store.UpsertAsync(_config.CollectionName, record, cancellationToken);
        }
    }

    public async Task ClearCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (await _store.CollectionExistsAsync(_config.CollectionName, cancellationToken))
            await _store.DeleteCollectionAsync(_config.CollectionName, cancellationToken);
        _collectionInitialized = false;
    }
}

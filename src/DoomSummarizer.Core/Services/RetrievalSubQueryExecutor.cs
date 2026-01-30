using DoomSummarizer.Models;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
/// Bridges the decomposer's ISubQueryExecutor to the existing DoomSummarizer
/// retrieval pipeline. Routes sub-queries through RetrievalPipeline.SearchAsync
/// and tool actions through the appropriate service.
/// </summary>
public sealed class RetrievalSubQueryExecutor : ISubQueryExecutor
{
    private readonly RetrievalPipeline _retrieval;
    private readonly IEmbeddingService _embedding;
    private readonly StorageService _storage;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RetrievalSubQueryExecutor>? _logger;

    /// <summary>Default retrieval options applied to each sub-query.</summary>
    private readonly RetrievalOptions _baseOptions;

    public RetrievalSubQueryExecutor(
        RetrievalPipeline retrieval,
        IEmbeddingService embedding,
        StorageService storage,
        HttpClient? httpClient = null,
        RetrievalOptions? baseOptions = null,
        ILogger<RetrievalSubQueryExecutor>? logger = null)
    {
        _retrieval = retrieval;
        _embedding = embedding;
        _storage = storage;
        _httpClient = httpClient ?? new HttpClient();
        _baseOptions = baseOptions ?? new RetrievalOptions();
        _logger = logger;
    }

    /// <summary>
    /// Execute a sub-query through the retrieval pipeline (Lucene FTS + embedding HNSW + RRF).
    /// </summary>
    public async Task<SubQueryResult> ExecuteAsync(QueryNode node, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Executing sub-query [{NodeId}]: {Query}", node.Id, node.Query);

            var options = _baseOptions with
            {
                TopK = _baseOptions.TopK > 0 ? _baseOptions.TopK : 10,
                UseEmbeddingDedup = true
            };

            var result = await _retrieval.SearchAsync(node.Query, options, ct);

            _logger?.LogDebug("Sub-query [{NodeId}] returned {Count} items", node.Id, result.Items.Count);

            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = true,
                Items = result.Items,
                ItemCount = result.Items.Count
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Sub-query [{NodeId}] failed: {Query}", node.Id, node.Query);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Fetch content from a direct reference (URL, file path, DOI).
    /// URLs are fetched via ContentExtractor; file paths are read directly.
    /// </summary>
    public async Task<SubQueryResult> FetchReferenceAsync(ContentReference reference, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Fetching reference: {Kind} {Uri}", reference.Kind, reference.Uri);

            switch (reference.Kind)
            {
                case ContentReferenceKind.Url:
                case ContentReferenceKind.Doi:
                case ContentReferenceKind.GitHubReference:
                    // Use ContentExtractor (SmartReader) to fetch and parse the URL
                    var extractor = new ContentExtractor(_httpClient);
                    var extracted = await extractor.ExtractAsync(reference.Uri, ct);
                    if (extracted == null || string.IsNullOrWhiteSpace(extracted.Content))
                    {
                        return new SubQueryResult
                        {
                            NodeId = reference.Uri,
                            Success = false,
                            Error = $"No content extracted from {reference.Uri}"
                        };
                    }

                    var item = new ContentItem
                    {
                        Id = $"ref:{reference.Uri.GetHashCode():X8}",
                        Source = $"reference:{reference.Kind.ToString().ToLowerInvariant()}",
                        Title = extracted.Title ?? reference.Uri,
                        Url = reference.Uri,
                        Content = extracted.Content,
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    return new SubQueryResult
                    {
                        NodeId = reference.Uri,
                        Success = true,
                        Items = new List<ContentItem> { item },
                        ItemCount = 1
                    };

                case ContentReferenceKind.FilePath:
                    if (!File.Exists(reference.Uri))
                    {
                        return new SubQueryResult
                        {
                            NodeId = reference.Uri,
                            Success = false,
                            Error = $"File not found: {reference.Uri}"
                        };
                    }

                    var fileContent = await File.ReadAllTextAsync(reference.Uri, ct);
                    var fileName = Path.GetFileName(reference.Uri);

                    var fileItem = new ContentItem
                    {
                        Id = $"file:{reference.Uri.GetHashCode():X8}",
                        Source = "reference:filepath",
                        Title = fileName,
                        Url = reference.Uri,
                        Content = fileContent,
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    return new SubQueryResult
                    {
                        NodeId = reference.Uri,
                        Success = true,
                        Items = new List<ContentItem> { fileItem },
                        ItemCount = 1
                    };

                default:
                    return new SubQueryResult
                    {
                        NodeId = reference.Uri,
                        Success = false,
                        Error = $"Unsupported reference kind: {reference.Kind}"
                    };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch reference: {Uri}", reference.Uri);
            return new SubQueryResult
            {
                NodeId = reference.Uri,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Execute a tool action. Currently supports Search and KbQuery via the retrieval pipeline.
    /// FileSystem, Index, Analyze, Crawl, and Transform return descriptive errors
    /// (tool implementations will be added as they are built).
    /// </summary>
    public async Task<SubQueryResult> ExecuteToolAsync(QueryNode node, ToolAction action, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Executing tool [{Tool}] for [{NodeId}]: {Intent}",
                action.Tool, node.Id, action.Intent);

            return action.Tool switch
            {
                ToolKind.Search => await ExecuteSearchToolAsync(node, action, ct),
                ToolKind.KbQuery => await ExecuteKbQueryToolAsync(node, action, ct),
                ToolKind.Fetch => await ExecuteFetchToolAsync(node, action, ct),
                _ => new SubQueryResult
                {
                    NodeId = node.Id,
                    Success = false,
                    Error = $"Tool '{action.Tool}' is not yet implemented. " +
                            $"Intent: {action.Intent}. " +
                            $"Parameters: {string.Join(", ", action.Parameters.Select(p => $"{p.Key}={p.Value}"))}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tool [{Tool}] failed for [{NodeId}]", action.Tool, node.Id);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public Task<bool> SupportsToolAsync(ToolKind tool, CancellationToken ct = default)
    {
        var supported = tool is ToolKind.Search or ToolKind.KbQuery or ToolKind.Fetch;
        return Task.FromResult(supported);
    }

    private async Task<SubQueryResult> ExecuteSearchToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var searchQuery = action.Parameters.GetValueOrDefault("query", node.Query);
        var options = _baseOptions with
        {
            TopK = int.TryParse(action.Parameters.GetValueOrDefault("max_items"), out var maxItems)
                ? maxItems
                : _baseOptions.TopK
        };

        var result = await _retrieval.SearchAsync(searchQuery, options, ct);
        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = result.Items,
            ItemCount = result.Items.Count
        };
    }

    private async Task<SubQueryResult> ExecuteKbQueryToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var collection = action.Parameters.GetValueOrDefault("collection", "default");
        var topK = int.TryParse(action.Parameters.GetValueOrDefault("top_k"), out var k) ? k : 10;

        var options = _baseOptions with
        {
            CollectionName = collection,
            TopK = topK,
            IsKnowledgeBase = true
        };

        var result = await _retrieval.SearchAsync(node.Query, options, ct);
        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = result.Items,
            ItemCount = result.Items.Count
        };
    }

    private async Task<SubQueryResult> ExecuteFetchToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var url = action.Parameters.GetValueOrDefault("url", node.Query);
        var reference = new ContentReference
        {
            Uri = url,
            Kind = ContentReferenceKind.Url,
            OriginalText = url
        };
        return await FetchReferenceAsync(reference, ct);
    }
}

using System.Diagnostics;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using LucidRAG.Services.Sentinel;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Web.Services;

/// <summary>
///     Explorer search service for semantic document search without LLM chat synthesis.
///     Uses Sentinel for query decomposition, returns ranked document results.
/// </summary>
public class ExplorerSearchService(
    ISentinelService sentinel,
    IAgenticSearchService searchService,
    RagDocumentsDbContext db,
    ILogger<ExplorerSearchService> logger) : IExplorerSearchService
{
    public async Task<ExplorerSearchResult> SearchAsync(
        ExplorerSearchRequest request,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Build schema context for Sentinel
        var schema = await sentinel.BuildSchemaContextAsync(request.CollectionId, ct);

        // Decompose the query using Sentinel
        var options = new SentinelOptions
        {
            CollectionId = request.CollectionId,
            MaxSubQueries = 5,
            ValidateAssumptions = false, // Skip validation for faster search
            MaxPlanningTime = TimeSpan.FromSeconds(3)
        };

        QueryPlan? queryPlan = null;
        var subQueries = new List<string> { request.Query };

        try
        {
            queryPlan = await sentinel.DecomposeAsync(request.Query, schema, options, ct);

            // Extract sub-queries from the plan
            if (queryPlan.SubQueries.Count > 0) subQueries = queryPlan.SubQueries.Select(sq => sq.Query).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentinel decomposition failed, using original query");
        }

        // Execute search for each sub-query and combine results
        var allResults = new List<ExplorerSearchResultItem>();
        var seenDocIds = new HashSet<Guid>();

        foreach (var subQuery in subQueries)
        {
            var searchRequest = new SearchRequest(
                subQuery,
                request.CollectionId,
                TopK: request.TopK,
                SearchMode: SearchMode.Hybrid);

            var searchResult = await searchService.SearchAsync(searchRequest, ct);

            foreach (var item in searchResult.Results)
                if (seenDocIds.Add(item.DocumentId))
                    allResults.Add(new ExplorerSearchResultItem
                    {
                        Id = item.DocumentId,
                        Type = "document", // TODO: Support other content types
                        Name = item.DocumentName,
                        Score = item.Score,
                        MatchedSegments = [item.Text],
                        SegmentId = item.SegmentId,
                        SectionTitle = item.SectionTitle
                    });
        }

        // Filter by content types if specified
        if (request.ContentTypes?.Length > 0)
            allResults = allResults
                .Where(r => request.ContentTypes.Contains(r.Type))
                .ToList();

        // Filter by community if specified
        if (request.CommunityId.HasValue)
        {
            var communityDocIds = await GetDocumentIdsByCommunityAsync(request.CommunityId.Value, ct);
            allResults = allResults
                .Where(r => communityDocIds.Contains(r.Id))
                .ToList();
        }

        // Enrich results with document details
        var docIds = allResults.Select(r => r.Id).Distinct().ToList();
        var documents = await db.Documents
            .Where(d => docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        foreach (var result in allResults)
            if (documents.TryGetValue(result.Id, out var doc))
            {
                result.MimeType = doc.MimeType;
                result.FileSizeBytes = doc.FileSizeBytes;
                result.Status = doc.Status.ToString().ToLowerInvariant();
                result.CreatedAt = doc.CreatedAt;
            }

        // Extract highlights from query decomposition
        var highlights = queryPlan?.SubQueries
            .Select(sq => sq.Purpose)
            .Distinct()
            .Take(10)
            .ToList() ?? [];

        stopwatch.Stop();

        return new ExplorerSearchResult
        {
            Results = allResults.OrderByDescending(r => r.Score).Take(request.TopK).ToList(),
            QueryDecomposition = subQueries,
            Highlights = highlights,
            TotalResults = allResults.Count,
            ResponseTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<HashSet<Guid>> GetDocumentIdsByCommunityAsync(Guid communityId, CancellationToken ct)
    {
        // Get entities in the community
        var entityIds = await db.Set<CommunityMembership>()
            .Where(cm => cm.CommunityId == communityId)
            .Select(cm => cm.EntityId)
            .ToListAsync(ct);

        // Get documents linked to those entities
        var docIds = await db.Set<DocumentEntityLink>()
            .Where(del => entityIds.Contains(del.EntityId))
            .Select(del => del.DocumentId)
            .Distinct()
            .ToHashSetAsync(ct);

        return docIds;
    }
}

public interface IExplorerSearchService
{
    Task<ExplorerSearchResult> SearchAsync(ExplorerSearchRequest request, CancellationToken ct = default);
}

public record ExplorerSearchRequest
{
    public required string Query { get; init; }
    public Guid? CollectionId { get; init; }
    public Guid? CommunityId { get; init; }
    public string[]? ContentTypes { get; init; }
    public int TopK { get; init; } = 50;
}

public record ExplorerSearchResult
{
    public List<ExplorerSearchResultItem> Results { get; init; } = [];
    public List<string> QueryDecomposition { get; init; } = [];
    public List<string> Highlights { get; init; } = [];
    public int TotalResults { get; init; }
    public long ResponseTimeMs { get; init; }
}

public record ExplorerSearchResultItem
{
    public Guid Id { get; init; }
    public required string Type { get; set; }
    public required string Name { get; set; }
    public double Score { get; init; }
    public List<string> MatchedSegments { get; init; } = [];
    public string? SegmentId { get; set; }
    public string? SectionTitle { get; set; }
    public string? MimeType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
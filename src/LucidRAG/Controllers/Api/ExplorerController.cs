using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Filters;
using LucidRAG.Services;
using LucidRAG.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Controllers.Api;

/// <summary>
///     File Explorer API - intelligent document browser with selection, filtering, and search.
///     Supports scoping chat/search to selected documents.
/// </summary>
[ApiController]
[Route("api/explorer")]
public class ExplorerController(
    IFolderService folderService,
    IExplorerSearchService searchService,
    RagDocumentsDbContext db,
    ILogger<ExplorerController> logger) : ControllerBase
{
    /// <summary>
    ///     Get explorer contents (folders and documents) with filtering
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetContents(
        [FromQuery] Guid? collectionId = null,
        [FromQuery] Guid? folderId = null,
        [FromQuery] Guid? communityId = null,
        [FromQuery] string? sort = "name",
        [FromQuery] string? order = "asc",
        [FromQuery] string? filterType = null, // pdf,docx,image,audio
        [FromQuery] string? filterStatus = null, // completed,processing,failed,pending
        [FromQuery] string? glob = null, // *.pdf, docs/*.md
        [FromQuery] bool? hasImages = null, // Signal: has images
        [FromQuery] bool? hasTables = null, // Signal: has tables
        [FromQuery] bool? hasCode = null, // Signal: has code (reserved)
        [FromQuery] string? dateRange = null, // 7d, 30d, 90d, 1y
        [FromQuery] string? entityIds = null, // Comma-separated entity GUIDs
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Build breadcrumb path
        var path = await folderService.GetPathAsync(folderId, collectionId, ct);

        // Get folders at this level
        var folders = collectionId.HasValue
            ? await folderService.GetFoldersAsync(collectionId.Value, folderId, ct)
            : [];

        var folderDtos = new List<object>();
        foreach (var folder in folders)
        {
            var itemCount = await folderService.GetItemCountAsync(folder.Id, ct);
            folderDtos.Add(new
            {
                id = folder.Id,
                name = folder.Name,
                type = "folder",
                itemCount,
                createdAt = folder.CreatedAt,
                updatedAt = folder.UpdatedAt
            });
        }

        // Build document query
        var docQuery = db.Documents.AsQueryable();

        if (collectionId.HasValue)
            docQuery = docQuery.Where(d => d.CollectionId == collectionId);

        if (folderId.HasValue)
            docQuery = docQuery.Where(d => d.FolderId == folderId);
        else if (collectionId.HasValue)
            docQuery = docQuery.Where(d => d.FolderId == null); // Root level only

        // Apply community filter (documents linked to entities in the community)
        if (communityId.HasValue)
        {
            var communityDocIds = await GetDocumentIdsByCommunityAsync(communityId.Value, ct);
            docQuery = docQuery.Where(d => communityDocIds.Contains(d.Id));
        }

        // Apply type filter
        if (!string.IsNullOrEmpty(filterType))
        {
            var types = filterType.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var mimeTypes = types.SelectMany(GetMimeTypesForFilter).ToList();
            if (mimeTypes.Count > 0)
                docQuery = docQuery.Where(d => d.MimeType != null && mimeTypes.Contains(d.MimeType));
        }

        // Apply status filter
        if (!string.IsNullOrEmpty(filterStatus))
        {
            var statuses = filterStatus.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Enum.TryParse<DocumentStatus>(s, true, out var status) ? status : (DocumentStatus?)null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToList();

            if (statuses.Count > 0)
                docQuery = docQuery.Where(d => statuses.Contains(d.Status));
        }

        // Apply glob pattern filter
        if (!string.IsNullOrEmpty(glob))
        {
            var pattern = GlobToLikePattern(glob);
            docQuery = docQuery.Where(d => EF.Functions.ILike(d.Name, pattern) ||
                                           (d.OriginalFilename != null &&
                                            EF.Functions.ILike(d.OriginalFilename, pattern)));
        }

        // Apply signal filters
        if (hasImages == true)
            // Filter documents that are images or have embedded images
            // Check MIME type for images, or has image-related records
            docQuery = docQuery.Where(d =>
                d.MimeType != null && d.MimeType.StartsWith("image/"));

        if (hasTables == true)
            // Filter documents that have tables
            docQuery = docQuery.Where(d => d.TableCount > 0);

        // Apply date range filter
        if (!string.IsNullOrEmpty(dateRange))
        {
            var cutoffDate = dateRange.ToLowerInvariant() switch
            {
                "7d" => DateTime.UtcNow.AddDays(-7),
                "30d" => DateTime.UtcNow.AddDays(-30),
                "90d" => DateTime.UtcNow.AddDays(-90),
                "1y" => DateTime.UtcNow.AddYears(-1),
                _ => (DateTime?)null
            };

            if (cutoffDate.HasValue)
                docQuery = docQuery.Where(d => d.CreatedAt >= cutoffDate.Value);
        }

        // Apply entity filter (documents linked to specific entities)
        if (!string.IsNullOrEmpty(entityIds))
        {
            var entityGuidList = entityIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToList();

            if (entityGuidList.Count > 0)
            {
                var docIdsWithEntities = await db.DocumentEntityLinks
                    .Where(del => entityGuidList.Contains(del.EntityId))
                    .Select(del => del.DocumentId)
                    .Distinct()
                    .ToListAsync(ct);

                docQuery = docQuery.Where(d => docIdsWithEntities.Contains(d.Id));
            }
        }

        // Apply sorting
        docQuery = (sort?.ToLowerInvariant(), order?.ToLowerInvariant()) switch
        {
            ("name", "desc") => docQuery.OrderByDescending(d => d.Name),
            ("date", "asc") => docQuery.OrderBy(d => d.CreatedAt),
            ("date", "desc") => docQuery.OrderByDescending(d => d.CreatedAt),
            ("size", "asc") => docQuery.OrderBy(d => d.FileSizeBytes),
            ("size", "desc") => docQuery.OrderByDescending(d => d.FileSizeBytes),
            ("status", "asc") => docQuery.OrderBy(d => d.Status),
            ("status", "desc") => docQuery.OrderByDescending(d => d.Status),
            _ => docQuery.OrderBy(d => d.Name)
        };

        // Get total count before pagination
        var totalDocuments = await docQuery.CountAsync(ct);

        // Apply pagination
        var documents = await docQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                originalFilename = d.OriginalFilename,
                type = "document",
                contentType = GetContentType(d.MimeType),
                mimeType = d.MimeType,
                status = d.Status.ToString().ToLowerInvariant(),
                statusMessage = d.StatusMessage,
                progress = d.ProcessingProgress,
                fileSizeBytes = d.FileSizeBytes,
                segmentCount = d.SegmentCount,
                entityCount = d.EntityCount,
                tableCount = d.TableCount,
                sourceUrl = d.SourceUrl,
                createdAt = d.CreatedAt,
                processedAt = d.ProcessedAt
            })
            .ToListAsync(ct);

        // Get communities for filter dropdown
        var communities = collectionId.HasValue
            ? await db.Communities
                .Where(c => c.CollectionId == collectionId)
                .OrderByDescending(c => c.EntityCount)
                .Take(50)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    entityCount = c.EntityCount,
                    level = c.Level
                })
                .ToListAsync(ct)
            : [];

        // Check if user can write (authenticated + not demo mode)
        var canWrite = User.Identity?.IsAuthenticated == true &&
                       !HttpContext.Items.ContainsKey("DemoMode");

        return Ok(new
        {
            path = path.Select(p => new { id = p.Id, name = p.Name, type = p.Type }),
            folders = folderDtos,
            documents,
            communities,
            pagination = new
            {
                page,
                pageSize,
                totalDocuments,
                totalPages = (int)Math.Ceiling((double)totalDocuments / pageSize)
            },
            canWrite
        });
    }

    /// <summary>
    ///     Semantic search within the explorer (no chat synthesis)
    /// </summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] ExplorerSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return BadRequest(new { error = "Query is required" });

        var result = await searchService.SearchAsync(request, ct);

        return Ok(new
        {
            results = result.Results.Select(r => new
            {
                id = r.Id,
                type = r.Type,
                name = r.Name,
                score = r.Score,
                matchedSegments = r.MatchedSegments,
                sectionTitle = r.SectionTitle,
                mimeType = r.MimeType,
                fileSizeBytes = r.FileSizeBytes,
                status = r.Status,
                createdAt = r.CreatedAt
            }),
            queryDecomposition = result.QueryDecomposition,
            highlights = result.Highlights,
            totalResults = result.TotalResults,
            responseTimeMs = result.ResponseTimeMs
        });
    }

    /// <summary>
    ///     Get document details with segments, entities, and evidence
    /// </summary>
    [HttpGet("documents/{id:guid}")]
    public async Task<IActionResult> GetDocumentDetails(Guid id, CancellationToken ct = default)
    {
        var document = await db.Documents
            .Include(d => d.Collection)
            .Include(d => d.Folder)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document is null) return NotFound(new { error = "Document not found" });

        // Get entities linked to this document
        var entities = await db.DocumentEntityLinks
            .Where(del => del.DocumentId == id)
            .Include(del => del.Entity)
            .OrderByDescending(del => del.MentionCount)
            .Take(50)
            .Select(del => new
            {
                id = del.Entity.Id,
                name = del.Entity.CanonicalName,
                type = del.Entity.EntityType,
                mentionCount = del.MentionCount
            })
            .ToListAsync(ct);

        // Get relationships involving these entities
        var entityIds = entities.Select(e => e.id).ToList();
        var relationships = await db.EntityRelationships
            .Where(r => entityIds.Contains(r.SourceEntityId) || entityIds.Contains(r.TargetEntityId))
            .Include(r => r.SourceEntity)
            .Include(r => r.TargetEntity)
            .Take(100)
            .Select(r => new
            {
                sourceId = r.SourceEntityId,
                sourceName = r.SourceEntity.CanonicalName,
                targetId = r.TargetEntityId,
                targetName = r.TargetEntity.CanonicalName,
                type = r.RelationshipType,
                strength = r.Strength
            })
            .ToListAsync(ct);

        // Get communities this document belongs to (via its entities)
        var communityIds = await db.CommunityMemberships
            .Where(cm => entityIds.Contains(cm.EntityId))
            .Select(cm => cm.CommunityId)
            .Distinct()
            .ToListAsync(ct);

        var communities = await db.Communities
            .Where(c => communityIds.Contains(c.Id))
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                level = c.Level
            })
            .ToListAsync(ct);

        return Ok(new
        {
            document = new
            {
                id = document.Id,
                name = document.Name,
                originalFilename = document.OriginalFilename,
                collectionId = document.CollectionId,
                collectionName = document.Collection?.Name,
                folderId = document.FolderId,
                folderName = document.Folder?.Name,
                status = document.Status.ToString().ToLowerInvariant(),
                statusMessage = document.StatusMessage,
                progress = document.ProcessingProgress,
                mimeType = document.MimeType,
                fileSizeBytes = document.FileSizeBytes,
                segmentCount = document.SegmentCount,
                entityCount = document.EntityCount,
                tableCount = document.TableCount,
                sourceUrl = document.SourceUrl,
                sourcePath = document.SourcePath,
                createdAt = document.CreatedAt,
                processedAt = document.ProcessedAt
            },
            entities,
            relationships,
            communities
        });
    }

    /// <summary>
    ///     Move documents to a folder
    /// </summary>
    [HttpPost("documents/move")]
    [DemoModeWriteBlock]
    public async Task<IActionResult> MoveDocuments([FromBody] MoveDocumentsRequest request,
        CancellationToken ct = default)
    {
        if (request.DocumentIds.Length == 0) return BadRequest(new { error = "No documents specified" });

        // Validate target folder exists and get its collection
        Guid? targetCollectionId = null;
        if (request.FolderId.HasValue)
        {
            var folder = await folderService.GetFolderAsync(request.FolderId.Value, ct);
            if (folder is null) return NotFound(new { error = "Target folder not found" });
            targetCollectionId = folder.CollectionId;
        }

        var documents = await db.Documents
            .Where(d => request.DocumentIds.Contains(d.Id))
            .ToListAsync(ct);

        if (documents.Count == 0) return BadRequest(new { error = "No valid documents found" });

        // Move documents to target folder
        foreach (var doc in documents)
        {
            doc.FolderId = request.FolderId;
            // Optionally update collection if moving to folder in different collection
            if (targetCollectionId.HasValue && doc.CollectionId != targetCollectionId)
                doc.CollectionId = targetCollectionId;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Moved {Count} documents to folder {FolderId}", documents.Count, request.FolderId);

        return Ok(new
        {
            success = true,
            moved = documents.Count
        });
    }

    /// <summary>
    ///     Get entities grouped by type for sidebar filtering
    /// </summary>
    [HttpGet("entities")]
    public async Task<IActionResult> GetEntities(
        [FromQuery] Guid? collectionId = null,
        [FromQuery] string? type = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        // Base query - entities linked to documents in the collection
        IQueryable<ExtractedEntity> query;

        if (collectionId.HasValue)
        {
            // Get entity IDs that are linked to documents in this collection
            var entityIdsInCollection = db.DocumentEntityLinks
                .Where(del => del.Document.CollectionId == collectionId)
                .Select(del => del.EntityId)
                .Distinct();

            query = db.Entities.Where(e => entityIdsInCollection.Contains(e.Id));
        }
        else
        {
            query = db.Entities.AsQueryable();
        }

        if (!string.IsNullOrEmpty(type))
            query = query.Where(e => e.EntityType == type);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(e => EF.Functions.ILike(e.CanonicalName, $"%{search}%"));

        // Get entity type counts
        var typeCountsQuery = collectionId.HasValue
            ? db.Entities.Where(e => db.DocumentEntityLinks
                .Any(del => del.EntityId == e.Id && del.Document.CollectionId == collectionId))
            : db.Entities;

        var typeCounts = await typeCountsQuery
            .GroupBy(e => e.EntityType)
            .Select(g => new { type = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToListAsync(ct);

        // Get entities with their mention counts, ordered by frequency
        var entitiesWithCounts = await query
            .Select(e => new
            {
                id = e.Id,
                name = e.CanonicalName,
                type = e.EntityType,
                description = e.Description,
                mentionCount = db.DocumentEntityLinks
                    .Where(del => del.EntityId == e.Id &&
                                  (!collectionId.HasValue || del.Document.CollectionId == collectionId))
                    .Sum(del => del.MentionCount)
            })
            .OrderByDescending(e => e.mentionCount)
            .Take(limit)
            .ToListAsync(ct);

        // Group entities by type for sidebar display
        var entitiesGroupedByType = entitiesWithCounts
            .GroupBy(e => e.type ?? "unknown")
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new
                {
                    e.id,
                    e.name,
                    e.description,
                    e.mentionCount
                }).ToList()
            );

        return Ok(new
        {
            typeCounts,
            entities = entitiesGroupedByType
        });
    }

    /// <summary>
    ///     Get available filter options (file types, date ranges, etc.)
    /// </summary>
    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters(
        [FromQuery] Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var docQuery = db.Documents.AsQueryable();

        if (collectionId.HasValue)
            docQuery = docQuery.Where(d => d.CollectionId == collectionId);

        // Get document type counts
        var typeGroups = await docQuery
            .Where(d => d.MimeType != null)
            .GroupBy(d => d.MimeType)
            .Select(g => new { mimeType = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var typeCounts = typeGroups
            .GroupBy(t => GetContentType(t.mimeType))
            .Select(g => new { type = g.Key, count = g.Sum(x => x.count) })
            .OrderByDescending(x => x.count)
            .ToList();

        // Get status counts
        var statusCounts = await docQuery
            .GroupBy(d => d.Status)
            .Select(g => new { status = g.Key.ToString().ToLowerInvariant(), count = g.Count() })
            .ToListAsync(ct);

        // Get date range
        var dateRange = await docQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                oldest = g.Min(d => d.CreatedAt),
                newest = g.Max(d => d.CreatedAt)
            })
            .FirstOrDefaultAsync(ct);

        // Get communities for filter
        var communities = collectionId.HasValue
            ? await db.Communities
                .Where(c => c.CollectionId == collectionId)
                .OrderByDescending(c => c.EntityCount)
                .Take(30)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    summary = c.Summary,
                    entityCount = c.EntityCount
                })
                .ToListAsync(ct)
            : [];

        return Ok(new
        {
            types = typeCounts,
            statuses = statusCounts,
            dateRange = new
            {
                dateRange?.oldest, dateRange?.newest
            },
            communities
        });
    }

    /// <summary>
    ///     Get stats for the current view (for status bar)
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] Guid? collectionId = null,
        [FromQuery] Guid? folderId = null,
        CancellationToken ct = default)
    {
        var docQuery = db.Documents.AsQueryable();

        if (collectionId.HasValue)
            docQuery = docQuery.Where(d => d.CollectionId == collectionId);

        if (folderId.HasValue)
            docQuery = docQuery.Where(d => d.FolderId == folderId);

        var stats = await docQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                totalDocuments = g.Count(),
                completedCount = g.Count(d => d.Status == DocumentStatus.Completed),
                processingCount = g.Count(d => d.Status == DocumentStatus.Processing),
                pendingCount = g.Count(d => d.Status == DocumentStatus.Pending),
                failedCount = g.Count(d => d.Status == DocumentStatus.Failed),
                totalSegments = g.Sum(d => d.SegmentCount),
                totalEntities = g.Sum(d => d.EntityCount),
                totalTables = g.Sum(d => d.TableCount),
                totalSizeBytes = g.Sum(d => d.FileSizeBytes ?? 0)
            })
            .FirstOrDefaultAsync(ct);

        var folderCount = collectionId.HasValue
            ? await db.Folders.CountAsync(f => f.CollectionId == collectionId && f.ParentFolderId == folderId, ct)
            : 0;

        return Ok(new
        {
            folders = folderCount,
            documents = stats?.totalDocuments ?? 0,
            completed = stats?.completedCount ?? 0,
            processing = stats?.processingCount ?? 0,
            pending = stats?.pendingCount ?? 0,
            failed = stats?.failedCount ?? 0,
            segments = stats?.totalSegments ?? 0,
            entities = stats?.totalEntities ?? 0,
            tables = stats?.totalTables ?? 0,
            totalSizeBytes = stats?.totalSizeBytes ?? 0
        });
    }

    // Helper methods

    private async Task<HashSet<Guid>> GetDocumentIdsByCommunityAsync(Guid communityId, CancellationToken ct)
    {
        var entityIds = await db.CommunityMemberships
            .Where(cm => cm.CommunityId == communityId)
            .Select(cm => cm.EntityId)
            .ToListAsync(ct);

        var docIds = await db.DocumentEntityLinks
            .Where(del => entityIds.Contains(del.EntityId))
            .Select(del => del.DocumentId)
            .Distinct()
            .ToHashSetAsync(ct);

        return docIds;
    }

    private static List<string> GetMimeTypesForFilter(string filter)
    {
        return filter.ToLowerInvariant() switch
        {
            "pdf" => ["application/pdf"],
            "docx" => ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            "doc" => ["application/msword"],
            "md" or "markdown" => ["text/markdown", "text/x-markdown"],
            "txt" or "text" => ["text/plain"],
            "html" => ["text/html"],
            "image" => ["image/jpeg", "image/png", "image/gif", "image/webp", "image/svg+xml"],
            "audio" => ["audio/mpeg", "audio/wav", "audio/ogg", "audio/mp4"],
            "video" => ["video/mp4", "video/webm", "video/ogg"],
            "csv" => ["text/csv"],
            "json" => ["application/json"],
            "excel" =>
                ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.ms-excel"],
            _ => []
        };
    }

    private static string GetContentType(string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "application/pdf" => "pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "application/msword" => "doc",
            "text/markdown" or "text/x-markdown" => "markdown",
            "text/plain" => "text",
            "text/html" => "html",
            "text/csv" => "csv",
            "application/json" => "json",
            var m when m?.StartsWith("image/") == true => "image",
            var m when m?.StartsWith("audio/") == true => "audio",
            var m when m?.StartsWith("video/") == true => "video",
            _ => "document"
        };
    }

    private static string GlobToLikePattern(string glob)
    {
        // Convert glob pattern to SQL LIKE pattern
        // * -> % (match any characters)
        // ? -> _ (match single character)
        return glob
            .Replace("%", "[%]")
            .Replace("_", "[_]")
            .Replace("*", "%")
            .Replace("?", "_");
    }
}

// Request DTOs
public record MoveDocumentsRequest(
    Guid[] DocumentIds,
    Guid? FolderId);
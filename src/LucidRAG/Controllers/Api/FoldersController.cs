using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Filters;
using LucidRAG.Services;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/folders")]
[DemoModeWriteBlock]
public class FoldersController(
    IFolderService folderService,
    RagDocumentsDbContext db,
    ILogger<FoldersController> logger) : ControllerBase
{
    /// <summary>
    ///     List folders in a collection, optionally filtered by parent folder
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid collectionId,
        [FromQuery] Guid? parentId = null,
        CancellationToken ct = default)
    {
        var folders = await folderService.GetFoldersAsync(collectionId, parentId, ct);

        // Get item counts for each folder
        var folderDtos = new List<object>();
        foreach (var folder in folders)
        {
            var itemCount = await folderService.GetItemCountAsync(folder.Id, ct);
            folderDtos.Add(new
            {
                id = folder.Id,
                name = folder.Name,
                description = folder.Description,
                parentFolderId = folder.ParentFolderId,
                itemCount,
                sortOrder = folder.SortOrder,
                createdAt = folder.CreatedAt,
                updatedAt = folder.UpdatedAt
            });
        }

        return Ok(new { folders = folderDtos });
    }

    /// <summary>
    ///     Get a single folder with details
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var folder = await folderService.GetFolderAsync(id, ct);

        if (folder is null) return NotFound(new { error = "Folder not found" });

        var itemCount = await folderService.GetItemCountAsync(id, ct);

        return Ok(new
        {
            id = folder.Id,
            name = folder.Name,
            description = folder.Description,
            collectionId = folder.CollectionId,
            collectionName = folder.Collection?.Name,
            parentFolderId = folder.ParentFolderId,
            parentFolderName = folder.ParentFolder?.Name,
            itemCount,
            sortOrder = folder.SortOrder,
            createdAt = folder.CreatedAt,
            updatedAt = folder.UpdatedAt
        });
    }

    /// <summary>
    ///     Get breadcrumb path to a folder
    /// </summary>
    [HttpGet("{id:guid}/path")]
    public async Task<IActionResult> GetPath(Guid id, CancellationToken ct = default)
    {
        var folder = await folderService.GetFolderAsync(id, ct);
        if (folder is null) return NotFound(new { error = "Folder not found" });

        var path = await folderService.GetPathAsync(id, folder.CollectionId, ct);

        return Ok(new
        {
            path = path.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                type = p.Type
            })
        });
    }

    /// <summary>
    ///     Get folder tree for a collection
    /// </summary>
    [HttpGet("tree")]
    public async Task<IActionResult> GetTree([FromQuery] Guid collectionId, CancellationToken ct = default)
    {
        var tree = await folderService.GetFolderTreeAsync(collectionId, ct);

        object MapFolder(FolderEntity f)
        {
            return new
            {
                id = f.Id,
                name = f.Name,
                children = f.ChildFolders.Select(MapFolder).ToList()
            };
        }

        return Ok(new
        {
            folders = tree.Select(MapFolder)
        });
    }

    /// <summary>
    ///     Create a new folder
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFolderRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { error = "Name is required" });

        try
        {
            var folder = await folderService.CreateFolderAsync(
                request.CollectionId,
                request.Name.Trim(),
                request.ParentFolderId,
                request.Description?.Trim(),
                ct);

            logger.LogInformation("Created folder {FolderId} '{Name}' in collection {CollectionId}",
                folder.Id, folder.Name, request.CollectionId);

            return CreatedAtAction(nameof(Get), new { id = folder.Id }, new
            {
                id = folder.Id,
                name = folder.Name,
                description = folder.Description,
                collectionId = folder.CollectionId,
                parentFolderId = folder.ParentFolderId,
                createdAt = folder.CreatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Update a folder (rename, description, sort order)
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateFolderRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var folder = await folderService.UpdateFolderAsync(
                id,
                request.Name?.Trim(),
                request.Description?.Trim(),
                request.SortOrder,
                ct);

            return Ok(new
            {
                id = folder.Id,
                name = folder.Name,
                description = folder.Description,
                sortOrder = folder.SortOrder,
                updatedAt = folder.UpdatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Delete a folder (contents move to parent)
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var folder = await folderService.GetFolderAsync(id, ct);
        if (folder is null) return NotFound(new { error = "Folder not found" });

        await folderService.DeleteFolderAsync(id, ct);

        logger.LogInformation("Deleted folder {FolderId} '{Name}'", id, folder.Name);

        return Ok(new { success = true });
    }

    /// <summary>
    ///     Move a folder to a new parent
    /// </summary>
    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveFolderRequest request, CancellationToken ct = default)
    {
        try
        {
            await folderService.MoveFolderAsync(id, request.ParentFolderId, ct);

            logger.LogInformation("Moved folder {FolderId} to parent {ParentFolderId}", id, request.ParentFolderId);

            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

// Request DTOs
public record CreateFolderRequest(
    Guid CollectionId,
    string Name,
    Guid? ParentFolderId = null,
    string? Description = null);

public record UpdateFolderRequest(
    string? Name = null,
    string? Description = null,
    int? SortOrder = null);

public record MoveFolderRequest(
    Guid? ParentFolderId);
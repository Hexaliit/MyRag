using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Services;

public class FolderService(
    RagDocumentsDbContext db,
    ILogger<FolderService> logger) : IFolderService
{
    public async Task<List<FolderEntity>> GetFoldersAsync(Guid collectionId, Guid? parentFolderId = null, CancellationToken ct = default)
    {
        return await db.Folders
            .Where(f => f.CollectionId == collectionId && f.ParentFolderId == parentFolderId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name)
            .ToListAsync(ct);
    }

    public async Task<FolderEntity?> GetFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        return await db.Folders
            .Include(f => f.Collection)
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.Id == folderId, ct);
    }

    public async Task<List<FolderPathItem>> GetPathAsync(Guid? folderId, Guid? collectionId = null, CancellationToken ct = default)
    {
        var path = new List<FolderPathItem>();

        // Add root item
        if (collectionId.HasValue)
        {
            var collection = await db.Collections.FindAsync([collectionId], ct);
            if (collection != null)
            {
                path.Add(new FolderPathItem(null, "All Documents", "root"));
                path.Add(new FolderPathItem(collection.Id, collection.Name, "collection"));
            }
        }
        else
        {
            path.Add(new FolderPathItem(null, "All Documents", "root"));
        }

        if (!folderId.HasValue) return path;

        // Build folder path by walking up the tree
        var folderPath = new List<FolderPathItem>();
        var currentFolderId = folderId;

        while (currentFolderId.HasValue)
        {
            var folder = await db.Folders.FindAsync([currentFolderId], ct);
            if (folder == null) break;

            folderPath.Add(new FolderPathItem(folder.Id, folder.Name, "folder"));
            currentFolderId = folder.ParentFolderId;

            // Update collectionId if not provided
            if (!collectionId.HasValue && path.Count == 1)
            {
                var collection = await db.Collections.FindAsync([folder.CollectionId], ct);
                if (collection != null)
                {
                    path.Add(new FolderPathItem(collection.Id, collection.Name, "collection"));
                }
            }
        }

        // Reverse to get root-to-leaf order
        folderPath.Reverse();
        path.AddRange(folderPath);

        return path;
    }

    public async Task<FolderEntity> CreateFolderAsync(Guid collectionId, string name, Guid? parentFolderId = null, string? description = null, CancellationToken ct = default)
    {
        // Validate collection exists
        var collection = await db.Collections.FindAsync([collectionId], ct)
            ?? throw new InvalidOperationException($"Collection {collectionId} not found");

        // Validate parent folder exists and is in same collection
        if (parentFolderId.HasValue)
        {
            var parentFolder = await db.Folders.FindAsync([parentFolderId], ct)
                ?? throw new InvalidOperationException($"Parent folder {parentFolderId} not found");

            if (parentFolder.CollectionId != collectionId)
                throw new InvalidOperationException("Parent folder must be in the same collection");
        }

        // Check for duplicate name in same location
        var existingFolder = await db.Folders
            .FirstOrDefaultAsync(f =>
                f.CollectionId == collectionId &&
                f.ParentFolderId == parentFolderId &&
                f.Name == name, ct);

        if (existingFolder != null)
            throw new InvalidOperationException($"A folder named '{name}' already exists in this location");

        var folder = new FolderEntity
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            ParentFolderId = parentFolderId,
            Name = name,
            Description = description
        };

        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created folder {FolderId} '{Name}' in collection {CollectionId}",
            folder.Id, folder.Name, collectionId);

        return folder;
    }

    public async Task<FolderEntity> RenameFolderAsync(Guid folderId, string newName, CancellationToken ct = default)
    {
        return await UpdateFolderAsync(folderId, name: newName, ct: ct);
    }

    public async Task<FolderEntity> UpdateFolderAsync(Guid folderId, string? name = null, string? description = null, int? sortOrder = null, CancellationToken ct = default)
    {
        var folder = await db.Folders.FindAsync([folderId], ct)
            ?? throw new InvalidOperationException($"Folder {folderId} not found");

        if (name != null && name != folder.Name)
        {
            // Check for duplicate name
            var existingFolder = await db.Folders
                .FirstOrDefaultAsync(f =>
                    f.CollectionId == folder.CollectionId &&
                    f.ParentFolderId == folder.ParentFolderId &&
                    f.Name == name &&
                    f.Id != folderId, ct);

            if (existingFolder != null)
                throw new InvalidOperationException($"A folder named '{name}' already exists in this location");

            folder.Name = name;
        }

        if (description != null)
            folder.Description = description;

        if (sortOrder.HasValue)
            folder.SortOrder = sortOrder.Value;

        folder.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Updated folder {FolderId}", folderId);
        return folder;
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        var folder = await db.Folders
            .Include(f => f.ChildFolders)
            .Include(f => f.Documents)
            .FirstOrDefaultAsync(f => f.Id == folderId, ct);

        if (folder == null) return;

        // Move child folders to parent (or root)
        foreach (var childFolder in folder.ChildFolders)
        {
            childFolder.ParentFolderId = folder.ParentFolderId;
            childFolder.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Move documents to parent folder (or root) - FolderId becomes null if folder is at root
        foreach (var doc in folder.Documents)
        {
            doc.FolderId = folder.ParentFolderId;
        }

        db.Folders.Remove(folder);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted folder {FolderId} '{Name}'", folderId, folder.Name);
    }

    public async Task MoveFolderAsync(Guid folderId, Guid? newParentFolderId, CancellationToken ct = default)
    {
        var folder = await db.Folders.FindAsync([folderId], ct)
            ?? throw new InvalidOperationException($"Folder {folderId} not found");

        // Validate new parent exists and is in same collection
        if (newParentFolderId.HasValue)
        {
            var newParent = await db.Folders.FindAsync([newParentFolderId], ct)
                ?? throw new InvalidOperationException($"Target folder {newParentFolderId} not found");

            if (newParent.CollectionId != folder.CollectionId)
                throw new InvalidOperationException("Cannot move folder to a different collection");

            // Check for circular reference
            if (await IsDescendantOfAsync(newParentFolderId.Value, folderId, ct))
                throw new InvalidOperationException("Cannot move folder into its own descendant");
        }

        // Check for duplicate name in new location
        var existingFolder = await db.Folders
            .FirstOrDefaultAsync(f =>
                f.CollectionId == folder.CollectionId &&
                f.ParentFolderId == newParentFolderId &&
                f.Name == folder.Name &&
                f.Id != folderId, ct);

        if (existingFolder != null)
            throw new InvalidOperationException($"A folder named '{folder.Name}' already exists in the target location");

        folder.ParentFolderId = newParentFolderId;
        folder.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Moved folder {FolderId} to parent {ParentFolderId}", folderId, newParentFolderId);
    }

    public async Task<int> GetItemCountAsync(Guid folderId, CancellationToken ct = default)
    {
        var folderCount = await db.Folders.CountAsync(f => f.ParentFolderId == folderId, ct);
        var documentCount = await db.Documents.CountAsync(d => d.FolderId == folderId, ct);
        return folderCount + documentCount;
    }

    public async Task<List<FolderEntity>> GetFolderTreeAsync(Guid collectionId, CancellationToken ct = default)
    {
        // Get all folders for the collection in one query
        var allFolders = await db.Folders
            .Where(f => f.CollectionId == collectionId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name)
            .ToListAsync(ct);

        // Build tree structure - return only root folders (parent navigation is loaded)
        var rootFolders = allFolders.Where(f => f.ParentFolderId == null).ToList();

        // Link children to parents
        var folderDict = allFolders.ToDictionary(f => f.Id);
        foreach (var folder in allFolders.Where(f => f.ParentFolderId.HasValue))
        {
            if (folderDict.TryGetValue(folder.ParentFolderId!.Value, out var parent))
            {
                parent.ChildFolders.Add(folder);
            }
        }

        return rootFolders;
    }

    private async Task<bool> IsDescendantOfAsync(Guid folderId, Guid potentialAncestorId, CancellationToken ct)
    {
        var currentFolderId = folderId;
        var visitedIds = new HashSet<Guid>();

        while (currentFolderId != default)
        {
            if (currentFolderId == potentialAncestorId)
                return true;

            if (!visitedIds.Add(currentFolderId))
                break; // Circular reference detected

            var folder = await db.Folders.FindAsync([currentFolderId], ct);
            if (folder?.ParentFolderId == null)
                break;

            currentFolderId = folder.ParentFolderId.Value;
        }

        return false;
    }
}

using LucidRAG.Entities;

namespace LucidRAG.Services;

public record FolderPathItem(Guid? Id, string Name, string Type);

public interface IFolderService
{
    Task<List<FolderEntity>> GetFoldersAsync(Guid collectionId, Guid? parentFolderId = null,
        CancellationToken ct = default);

    Task<FolderEntity?> GetFolderAsync(Guid folderId, CancellationToken ct = default);
    Task<List<FolderPathItem>> GetPathAsync(Guid? folderId, Guid? collectionId = null, CancellationToken ct = default);

    Task<FolderEntity> CreateFolderAsync(Guid collectionId, string name, Guid? parentFolderId = null,
        string? description = null, CancellationToken ct = default);

    Task<FolderEntity> RenameFolderAsync(Guid folderId, string newName, CancellationToken ct = default);

    Task<FolderEntity> UpdateFolderAsync(Guid folderId, string? name = null, string? description = null,
        int? sortOrder = null, CancellationToken ct = default);

    Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default);
    Task MoveFolderAsync(Guid folderId, Guid? newParentFolderId, CancellationToken ct = default);
    Task<int> GetItemCountAsync(Guid folderId, CancellationToken ct = default);
    Task<List<FolderEntity>> GetFolderTreeAsync(Guid collectionId, CancellationToken ct = default);
}
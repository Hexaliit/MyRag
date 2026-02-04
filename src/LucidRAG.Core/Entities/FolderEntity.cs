namespace LucidRAG.Entities;

/// <summary>
///     Represents a virtual folder within a collection for organizing documents.
///     Folders can be nested to create a hierarchy.
/// </summary>
public class FolderEntity
{
    public Guid Id { get; set; }
    public Guid CollectionId { get; set; }

    /// <summary>
    ///     Parent folder ID. Null means this folder is at the root of the collection.
    /// </summary>
    public Guid? ParentFolderId { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    ///     Sort order within the parent folder. Lower values appear first.
    /// </summary>
    public int SortOrder { get; set; } = 0;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation properties
    public CollectionEntity Collection { get; set; } = null!;
    public FolderEntity? ParentFolder { get; set; }
    public ICollection<FolderEntity> ChildFolders { get; set; } = [];
    public ICollection<DocumentEntity> Documents { get; set; } = [];
}
using System.Text.Json;

namespace Mostlylucid.Storage.Core.Abstractions.Models;

public class VectorStoreRecord
{
    public required string Id { get; set; }
    public required string DocumentId { get; set; }
    public required string ChunkId { get; set; }
    public required float[] Embedding { get; set; }
    public string? Text { get; set; }
    public string? SourceFile { get; set; }
    public string? Language { get; set; }
    public string? Namespace { get; set; }
    public string? ParentId { get; set; }
    public string? ContentHash { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string SerializeMetadata()
    {
        return Metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(Metadata);
    }

    public static Dictionary<string, object> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
            return new Dictionary<string, object>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }
}

namespace Mostlylucid.Storage.Core.Abstractions.Models;

public class SearchResult
{
    public required string Id { get; set; }
    public required double Score { get; set; }
    public double? CosineScore { get; set; }
    public double? Bm25Score { get; set; }
    public VectorStoreRecord? Record { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public string? Text { get; set; }
}

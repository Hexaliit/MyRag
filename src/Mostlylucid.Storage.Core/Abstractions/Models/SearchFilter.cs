namespace Mostlylucid.Storage.Core.Abstractions.Models;

public class SearchFilter
{
    public int TopK { get; set; } = 10;
    public string? Namespace { get; set; }
    public string? DocumentId { get; set; }
    public string? Language { get; set; }
    public string? SourceFile { get; set; }
    public Dictionary<string, string>? MetadataFilter { get; set; }
    public double MinScore { get; set; } = 0.0;
}

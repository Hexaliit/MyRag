namespace DoomWriter.Models;

public record CrawlSource
{
    public required string Url { get; set; }
    public string Name { get; set; } = "";
    public int MaxDepth { get; set; } = 2;
    public int MaxPages { get; set; } = 50;
    public string? PathFilter { get; set; }
    public bool Recurse { get; set; } = true;
    public List<string> TargetCorpora { get; set; } = [];
    public DateTimeOffset? LastCrawled { get; set; }
}
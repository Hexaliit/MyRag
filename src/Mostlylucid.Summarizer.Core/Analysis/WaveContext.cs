namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
///     Shared context passed through all waves in a coordinator run.
/// </summary>
public sealed class WaveContext
{
    public required string DocumentId { get; init; }
    public string? FilePath { get; init; }
    public string? Url { get; init; }
    public string Domain { get; init; } = "any";
    public SignalBag Signals { get; } = new();
    public CacheBag Cache { get; } = new();
    public required IServiceProvider Services { get; init; }
    public IReadOnlyDictionary<string, object>? Config { get; init; }
    public Action<string>? Progress { get; init; }
    public string? Route { get; init; }

    public bool IsFastRoute => Route == "fast";
    public bool IsQualityRoute => Route == "quality";
}
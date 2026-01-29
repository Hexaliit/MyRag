using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Waves;

/// <summary>
/// Counts words in the document.
/// Fast lane, high priority — cheap computation.
/// </summary>
public sealed class WordCountWave : ITypedAnalysisWave<string>
{
    public string Name => "doc.metrics.wordcount";
    public string Description => "Count words in document";
    public int Priority => 80;
    public IReadOnlyList<string> Tags => [SignalTags.Metadata];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(string content, AnalysisContext context) => true;

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string markdown, AnalysisContext context, CancellationToken ct = default)
    {
        var wordCount = string.IsNullOrWhiteSpace(markdown)
            ? 0
            : markdown.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;

        var signals = new List<Signal>
        {
            new()
            {
                Key = "doc.metrics.word_count",
                Value = wordCount,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            }
        };

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }
}

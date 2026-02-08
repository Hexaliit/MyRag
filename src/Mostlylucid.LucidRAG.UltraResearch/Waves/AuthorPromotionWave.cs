using LucidRAG.UltraResearch.Signals;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Emits a signal indicating authors should be promoted to entities.
///     The actual promotion is done by the DocumentIngester during IngestWave.
///     Fast lane — signal emission only.
/// </summary>
public sealed class AuthorPromotionWave : ITypedAnalysisWave<FetchCandidate>
{
    public string Name => "research.author_promotion";
    public string Description => "Signal author promotion to graph entities";
    public int Priority => 20;
    public IReadOnlyList<string> Tags => ["fast", SignalTags.Entity];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        return context.HasSignal(ResearchSignalKeys.PaperMetadataAuthors) &&
               context.HasSignal(ResearchSignalKeys.PaperIngestSuccess) &&
               context.GetValue<bool>(ResearchSignalKeys.PaperIngestSuccess);
    }

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        var authors = context.GetValue<List<string>>(ResearchSignalKeys.PaperMetadataAuthors);
        if (authors is { Count: > 0 })
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperAuthorsPromoted,
                Value = authors,
                Confidence = 0.95,
                Source = Name,
                Tags = [SignalTags.Entity],
                Metadata = new Dictionary<string, object> { ["author_count"] = authors.Count }
            });
        }

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }
}

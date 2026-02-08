using DoomSummarizer.Services;
using LucidRAG.UltraResearch.Signals;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Extracts citation IDs (arXiv, DOI) from paper content text.
///     Fast lane — CPU-only regex extraction.
/// </summary>
public sealed class CitationExtractionWave : ITypedAnalysisWave<FetchCandidate>
{
    public string Name => "research.citation_extraction";
    public string Description => "Extract citation IDs from paper content";
    public int Priority => 50;
    public IReadOnlyList<string> Tags => ["fast", SignalTags.Entity];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        return context.HasSignal(ResearchSignalKeys.PaperContentText);
    }

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        var contentText = context.GetValue<string>(ResearchSignalKeys.PaperContentText);
        if (string.IsNullOrEmpty(contentText)) return Task.FromResult<IEnumerable<Signal>>(signals);

        var arxivId = context.GetValue<string>(ResearchSignalKeys.PaperMetadataArxivId);
        var citationIds = AcademicPatterns.ExtractCitationIds(contentText, arxivId);

        context.SetCached("citation_ids", citationIds);

        signals.Add(new Signal
        {
            Key = ResearchSignalKeys.PaperCitationsExtracted,
            Value = citationIds,
            Confidence = 0.8,
            Source = Name,
            Tags = [SignalTags.Entity],
            Metadata = new Dictionary<string, object> { ["count"] = citationIds.Count }
        });

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }
}

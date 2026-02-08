using DoomSummarizer.Services;
using LucidRAG.UltraResearch.Signals;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Resolves venue quality via Semantic Scholar lookup.
///     IO lane — makes S2 API calls.
/// </summary>
public sealed class VenueResolutionWave : ITypedAnalysisWave<FetchCandidate>
{
    private readonly SemanticScholarClient _semanticScholar;
    private readonly ILogger<VenueResolutionWave> _logger;

    public VenueResolutionWave(SemanticScholarClient semanticScholar, ILogger<VenueResolutionWave> logger)
    {
        _semanticScholar = semanticScholar;
        _logger = logger;
    }

    public string Name => "research.venue_resolution";
    public string Description => "Resolve venue quality and citation counts via Semantic Scholar";
    public int Priority => 70;
    public IReadOnlyList<string> Tags => ["io", SignalTags.Quality];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        return context.HasSignal(ResearchSignalKeys.PaperFetchSuccess) &&
               context.GetValue<bool>(ResearchSignalKeys.PaperFetchSuccess);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        try
        {
            var arxivId = context.GetValue<string>(ResearchSignalKeys.PaperMetadataArxivId);
            var doi = context.GetValue<string>(ResearchSignalKeys.PaperMetadataDoi);

            var s2Id = !string.IsNullOrEmpty(arxivId)
                ? SemanticScholarClient.ArxivToS2Id(arxivId)
                : !string.IsNullOrEmpty(doi)
                    ? SemanticScholarClient.DoiToS2Id(doi)
                    : null;

            if (s2Id == null) return signals;

            var s2Paper = await _semanticScholar.GetPaperAsync(s2Id, ct);
            if (s2Paper == null) return signals;

            context.SetCached("s2_paper", s2Paper);

            var crossRefMetadata = context.GetCached<DoomSummarizer.Models.CitationMetadata>("citation_metadata");
            var venueQuality = VenueQualityScorer.ComputeVenueQuality(s2Paper, crossRefMetadata);

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperVenueQuality,
                Value = venueQuality,
                Confidence = 0.85,
                Source = Name,
                Tags = [SignalTags.Quality]
            });

            if (!string.IsNullOrEmpty(s2Paper.PublicationVenue?.Name ?? s2Paper.Venue))
            {
                signals.Add(new Signal
                {
                    Key = ResearchSignalKeys.PaperVenueName,
                    Value = s2Paper.PublicationVenue?.Name ?? s2Paper.Venue,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = [SignalTags.Quality]
                });
            }

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperCitationsCount,
                Value = s2Paper.CitationCount,
                Confidence = 0.95,
                Source = Name,
                Tags = [SignalTags.Quality]
            });

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperCitationsInfluential,
                Value = s2Paper.InfluentialCitationCount,
                Confidence = 0.95,
                Source = Name,
                Tags = [SignalTags.Quality]
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VenueResolutionWave failed for {Type}:{Id}", candidate.Type, candidate.Id);
        }

        return signals;
    }
}

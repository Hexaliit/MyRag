using DoomSummarizer.Services;
using LucidRAG.UltraResearch.Signals;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Resolves paper metadata (title, authors, year, doi) from arXiv API or CrossRef.
///     IO lane — makes network calls to external APIs.
/// </summary>
public sealed class MetadataFetchWave : ITypedAnalysisWave<FetchCandidate>
{
    private readonly ICitationResolver _citationResolver;
    private readonly ILogger<MetadataFetchWave> _logger;

    public MetadataFetchWave(ICitationResolver citationResolver, ILogger<MetadataFetchWave> logger)
    {
        _citationResolver = citationResolver;
        _logger = logger;
    }

    public string Name => "research.metadata_fetch";
    public string Description => "Resolve paper metadata from arXiv/CrossRef";
    public int Priority => 90;
    public IReadOnlyList<string> Tags => ["io", SignalTags.Metadata];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        return !string.IsNullOrEmpty(content.Id);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        DoomSummarizer.Models.CitationMetadata? metadata = null;

        if (candidate.Type == "arxiv")
        {
            var arxivId = AcademicPatterns.StripArxivVersion(candidate.Id);
            metadata = await _citationResolver.ResolveArxivAsync(arxivId, ct);

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperMetadataArxivId,
                Value = arxivId,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }
        else if (candidate.Type == "doi")
        {
            var doi = AcademicPatterns.NormalizeDoi(candidate.Id);
            if (string.IsNullOrEmpty(doi)) doi = candidate.Id;
            metadata = await _citationResolver.ResolveDoiAsync(doi, ct);

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperMetadataDoi,
                Value = doi,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }

        if (metadata != null)
        {
            // Cache metadata for downstream waves
            context.SetCached("citation_metadata", metadata);

            if (!string.IsNullOrEmpty(metadata.Title))
                signals.Add(new Signal
                {
                    Key = ResearchSignalKeys.PaperMetadataTitle,
                    Value = metadata.Title,
                    Confidence = 0.95,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });

            if (metadata.Authors.Count > 0)
                signals.Add(new Signal
                {
                    Key = ResearchSignalKeys.PaperMetadataAuthors,
                    Value = metadata.Authors,
                    Confidence = 0.95,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });

            if (metadata.Year.HasValue)
                signals.Add(new Signal
                {
                    Key = ResearchSignalKeys.PaperMetadataYear,
                    Value = metadata.Year.Value,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });

            if (!string.IsNullOrEmpty(metadata.Doi))
                signals.Add(new Signal
                {
                    Key = ResearchSignalKeys.PaperMetadataDoi,
                    Value = metadata.Doi,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });

            if (!string.IsNullOrEmpty(metadata.ArxivId))
                signals.Add(new Signal
                {
                    Key = ResearchSignalKeys.PaperMetadataArxivId,
                    Value = metadata.ArxivId,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperFetchSuccess,
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }
        else
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperFetchSuccess,
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }

        _logger.LogDebug("MetadataFetchWave: {Type}:{Id} → {Signals} signals",
            candidate.Type, candidate.Id, signals.Count);

        return signals;
    }
}

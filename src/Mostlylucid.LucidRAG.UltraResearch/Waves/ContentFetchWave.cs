using DoomSummarizer.Services;
using LucidRAG.UltraResearch.Signals;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Fetches full text content from ar5iv or uses abstract as fallback.
///     IO lane — makes HTTP calls to ar5iv.
/// </summary>
public sealed class ContentFetchWave : ITypedAnalysisWave<FetchCandidate>
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ContentFetchWave> _logger;

    public ContentFetchWave(HttpClient httpClient, ILogger<ContentFetchWave> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Name => "research.content_fetch";
    public string Description => "Fetch full text from ar5iv or use abstract fallback";
    public int Priority => 80;
    public IReadOnlyList<string> Tags => ["io", SignalTags.Content];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        // Need metadata to have been fetched first
        return context.HasSignal(ResearchSignalKeys.PaperFetchSuccess) &&
               context.GetValue<bool>(ResearchSignalKeys.PaperFetchSuccess);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        string? content = null;

        var arxivId = context.GetValue<string>(ResearchSignalKeys.PaperMetadataArxivId);
        if (!string.IsNullOrEmpty(arxivId))
        {
            content = await AcademicPatterns.FetchAr5ivTextAsync(_httpClient, arxivId, ct);
        }

        // Fallback: use abstract from cached metadata
        if (string.IsNullOrEmpty(content))
        {
            var metadata = context.GetCached<DoomSummarizer.Models.CitationMetadata>("citation_metadata");
            content = metadata?.Abstract;
        }

        if (!string.IsNullOrEmpty(content))
        {
            context.SetCached("paper_content", content);

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperContentText,
                Value = content,
                Confidence = arxivId != null ? 0.9 : 0.6,
                Source = Name,
                Tags = [SignalTags.Content]
            });

            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperContentLength,
                Value = content.Length,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Content]
            });
        }

        _logger.LogDebug("ContentFetchWave: {Type}:{Id} → {Length} chars",
            candidate.Type, candidate.Id, content?.Length ?? 0);

        return signals;
    }
}

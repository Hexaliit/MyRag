using LucidRAG.UltraResearch.Signals;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Ingests the saved markdown file through the DocSummarizer pipeline.
///     ML lane — runs ONNX embeddings, limits concurrency.
/// </summary>
public sealed class IngestWave : ITypedAnalysisWave<FetchCandidate>
{
    private readonly ILogger<IngestWave> _logger;

    public IngestWave(ILogger<IngestWave> logger)
    {
        _logger = logger;
    }

    public string Name => "research.ingest";
    public string Description => "Ingest paper through DocSummarizer pipeline";
    public int Priority => 30;
    public IReadOnlyList<string> Tags => ["ml", SignalTags.Content];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        // Skip if DryRun or no file saved
        var isDryRun = context.GetCached<bool>("dry_run");
        return !isDryRun && context.HasSignal(ResearchSignalKeys.PaperFilePath);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        var filePath = context.GetValue<string>(ResearchSignalKeys.PaperFilePath);
        if (string.IsNullOrEmpty(filePath)) return signals;

        var ingester = context.GetCached<IDocumentIngester>("ingester");
        var collectionId = context.GetCached<Guid>("collection_id");
        if (ingester == null || collectionId == Guid.Empty) return signals;

        var result = await ingester.IngestAsync(filePath, collectionId, ct);

        signals.Add(new Signal
        {
            Key = ResearchSignalKeys.PaperIngestSuccess,
            Value = result.Success,
            Confidence = 1.0,
            Source = Name,
            Tags = [SignalTags.Content]
        });

        if (result.SegmentCount > 0)
        {
            signals.Add(new Signal
            {
                Key = ResearchSignalKeys.PaperIngestSegmentCount,
                Value = result.SegmentCount,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Content]
            });
        }

        if (!result.Success)
        {
            _logger.LogWarning("IngestWave: ingestion failed for {FilePath}: {Message}", filePath, result.Message);
        }

        return signals;
    }
}

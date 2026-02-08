using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Wraps <see cref="WaveCoordinator"/> with research-specific profiles and lane configuration.
///     Each paper is processed through the coordinator with its own AnalysisContext.
///     The io lane semaphore naturally rate-limits API calls across concurrent papers.
/// </summary>
public sealed class PaperCoordinator
{
    private readonly WaveCoordinator _coordinator;
    private readonly ILogger<PaperCoordinator> _logger;

    /// <summary>
    ///     Research-default profile: io lane=3 (rate limit respect), ml lane=1 (ONNX), 60s timeout.
    /// </summary>
    public static CoordinatorProfile ResearchDefault => new()
    {
        Name = "research-default",
        WaveTimeoutMs = 60_000,
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["fast"] = new() { Name = "fast", MaxConcurrency = 8 },
            ["io"] = new() { Name = "io", MaxConcurrency = 3 },
            ["ml"] = new() { Name = "ml", MaxConcurrency = 1 }
        }
    };

    /// <summary>
    ///     Fast/DryRun profile: io lane=5, no ml lane, 15s timeout.
    /// </summary>
    public static CoordinatorProfile ResearchFast => new()
    {
        Name = "research-fast",
        WaveTimeoutMs = 15_000,
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["fast"] = new() { Name = "fast", MaxConcurrency = 16 },
            ["io"] = new() { Name = "io", MaxConcurrency = 5 }
        }
    };

    public PaperCoordinator(
        IEnumerable<ITypedAnalysisWave<FetchCandidate>> waves,
        ILogger<PaperCoordinator> logger)
    {
        _coordinator = new WaveCoordinator();
        _logger = logger;

        foreach (var wave in waves)
            _coordinator.RegisterWave(wave);
    }

    /// <summary>
    ///     Process a single paper through all registered waves.
    /// </summary>
    /// <param name="candidate">The paper candidate to process.</param>
    /// <param name="sessionContext">Session-level context for cross-paper signal accumulation.</param>
    /// <param name="ingester">Document ingester for the IngestWave.</param>
    /// <param name="collectionId">Collection to ingest into.</param>
    /// <param name="dataDir">Directory for saving fetched papers.</param>
    /// <param name="dryRun">If true, skip ingestion and author promotion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Coordinator result with signals and execution log.</returns>
    public async Task<CoordinatorResult> ProcessAsync(
        FetchCandidate candidate,
        AnalysisContext? sessionContext,
        IDocumentIngester ingester,
        Guid collectionId,
        string dataDir,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var profile = dryRun ? ResearchFast : ResearchDefault;

        // Create per-paper context, pre-populate with shared data
        var context = new AnalysisContext();
        context.SetCached("ingester", ingester);
        context.SetCached("collection_id", collectionId);
        context.SetCached("data_dir", dataDir);
        context.SetCached("dry_run", dryRun);

        var result = await _coordinator.ExecuteAsync(candidate, context, profile, ct);

        // Accumulate signals into session context
        if (sessionContext != null)
        {
            sessionContext.AddSignals(result.Signals);
        }

        _logger.LogDebug(
            "PaperCoordinator: {Type}:{Id} completed in {Duration}ms, {SignalCount} signals, profile={Profile}",
            candidate.Type, candidate.Id, result.TotalDurationMs, result.Signals.Count, result.Profile);

        return result;
    }
}

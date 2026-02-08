using System.Text;
using System.Text.Json;
using DoomSummarizer.Services;
using LucidRAG.Data;
using LucidRAG.Services;
using LucidRAG.UltraResearch.Signals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch;

/// <summary>
///     LLM sentinel checkpoint that evaluates the "shape of the data" in a research corpus.
///     Identifies conceptual gaps, suggests new queries, and assesses convergence.
///     Gracefully degrades to structural-only mode when no LLM is available.
/// </summary>
public class ResearchSentinelEvaluator
{
    private readonly OllamaService? _ollama;
    private readonly RagDocumentsDbContext _db;
    private readonly CitationGraphQueries _graphQueries;
    private readonly ILogger<ResearchSentinelEvaluator> _logger;

    public ResearchSentinelEvaluator(
        RagDocumentsDbContext db,
        CitationGraphQueries graphQueries,
        ILogger<ResearchSentinelEvaluator> logger,
        OllamaService? ollama = null)
    {
        _db = db;
        _graphQueries = graphQueries;
        _logger = logger;
        _ollama = ollama;
    }

    /// <summary>
    ///     Run a sentinel evaluation on the current corpus state.
    /// </summary>
    public async Task<SentinelCheckpoint> EvaluateAsync(
        UltraResearchState state,
        Guid collectionId,
        CancellationToken ct = default)
    {
        var checkpoint = new SentinelCheckpoint
        {
            Iteration = state.Iteration,
            TotalPapers = state.PapersIngested
        };

        // Gather structural signals
        var orphans = await _graphQueries.FindOrphanCitationsAsync(collectionId, limit: 30, ct: ct);
        var foundational = await _graphQueries.FindFoundationalReferencesAsync(collectionId, limit: 20, ct: ct);
        checkpoint.OrphanCitations = orphans.Count;

        // Count entities in collection
        var entityCount = await _db.DocumentEntityLinks
            .Where(del => _db.Documents
                .Where(d => d.CollectionId == collectionId)
                .Select(d => d.Id)
                .Contains(del.DocumentId))
            .Select(del => del.EntityId)
            .Distinct()
            .CountAsync(ct);
        checkpoint.TotalEntities = entityCount;

        // Compute new info ratio: entities added in last batch vs total
        var previousEntityCount = state.Checkpoints.LastOrDefault()?.TotalEntities ?? 0;
        checkpoint.NewInfoRatio = entityCount > 0 && previousEntityCount > 0
            ? (double)(entityCount - previousEntityCount) / entityCount
            : 1.0; // First checkpoint: assume all info is new

        // Get top entities by type for LLM context
        var topEntities = await GetTopEntitiesByTypeAsync(collectionId, ct);

        // Get year distribution
        var yearDistribution = await GetYearDistributionAsync(collectionId, ct);

        // Try LLM evaluation, fall back to structural-only
        var llmAvailable = _ollama != null && await CheckLlmAvailableAsync();

        if (llmAvailable)
        {
            await RunLlmSentinelAsync(checkpoint, state, topEntities, yearDistribution, orphans, foundational, ct);
        }
        else
        {
            RunStructuralSentinel(checkpoint, state, orphans, yearDistribution);
        }

        _logger.LogDebug("Sentinel checkpoint: {Papers} papers, {Entities} entities, ratio={Ratio:F2}, continue={Continue}",
            checkpoint.TotalPapers, checkpoint.TotalEntities, checkpoint.NewInfoRatio, checkpoint.ShouldContinue);

        return checkpoint;
    }

    /// <summary>
    ///     Evaluate convergence using accumulated signals from the session AnalysisContext
    ///     instead of re-querying the database. Faster and more responsive to recent changes.
    /// </summary>
    public async Task<SentinelCheckpoint> EvaluateFromSignalsAsync(
        UltraResearchState state,
        AnalysisContext sessionContext,
        Guid collectionId,
        CancellationToken ct = default)
    {
        var checkpoint = new SentinelCheckpoint
        {
            Iteration = state.Iteration,
            TotalPapers = state.PapersIngested
        };

        // Count entities from DB (still needed for accurate count)
        var entityCount = await _db.DocumentEntityLinks
            .Where(del => _db.Documents
                .Where(d => d.CollectionId == collectionId)
                .Select(d => d.Id)
                .Contains(del.DocumentId))
            .Select(del => del.EntityId)
            .Distinct()
            .CountAsync(ct);
        checkpoint.TotalEntities = entityCount;

        // Compute new info ratio from entity count delta
        var previousEntityCount = state.Checkpoints.LastOrDefault()?.TotalEntities ?? 0;
        checkpoint.NewInfoRatio = entityCount > 0 && previousEntityCount > 0
            ? (double)(entityCount - previousEntityCount) / entityCount
            : 1.0;

        // Extract author frequency from accumulated signals
        var authorSignals = sessionContext.GetSignals(ResearchSignalKeys.PaperMetadataAuthors).ToList();
        var authorFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in authorSignals)
        {
            if (signal.Value is List<string> authors)
            {
                foreach (var author in authors)
                {
                    authorFrequency.TryGetValue(author, out var count);
                    authorFrequency[author] = count + 1;
                }
            }
        }

        // Emit session signals
        sessionContext.AddSignal(new Signal
        {
            Key = ResearchSignalKeys.SessionConvergenceNewInfoRatio,
            Value = checkpoint.NewInfoRatio,
            Confidence = 1.0,
            Source = "sentinel"
        });

        sessionContext.AddSignal(new Signal
        {
            Key = ResearchSignalKeys.SessionAuthorFrequency,
            Value = authorFrequency,
            Confidence = 1.0,
            Source = "sentinel"
        });

        // Fall through to standard evaluation for gap analysis
        var orphans = await _graphQueries.FindOrphanCitationsAsync(collectionId, limit: 30, ct: ct);
        checkpoint.OrphanCitations = orphans.Count;

        var llmAvailable = _ollama != null && await CheckLlmAvailableAsync();
        if (llmAvailable)
        {
            var topEntities = await GetTopEntitiesByTypeAsync(collectionId, ct);
            var yearDistribution = await GetYearDistributionAsync(collectionId, ct);
            var foundational = await _graphQueries.FindFoundationalReferencesAsync(collectionId, limit: 20, ct: ct);
            await RunLlmSentinelAsync(checkpoint, state, topEntities, yearDistribution, orphans, foundational, ct);
        }
        else
        {
            var yearDistribution = await GetYearDistributionAsync(collectionId, ct);
            RunStructuralSentinel(checkpoint, state, orphans, yearDistribution);
        }

        // Emit sentinel result signals
        sessionContext.AddSignal(new Signal
        {
            Key = ResearchSignalKeys.SentinelGaps,
            Value = checkpoint.IdentifiedGaps,
            Confidence = 1.0,
            Source = "sentinel"
        });

        sessionContext.AddSignal(new Signal
        {
            Key = ResearchSignalKeys.SentinelShouldContinue,
            Value = checkpoint.ShouldContinue,
            Confidence = 1.0,
            Source = "sentinel"
        });

        _logger.LogDebug("Signal-based sentinel: {Papers} papers, {Entities} entities, ratio={Ratio:F2}, continue={Continue}",
            checkpoint.TotalPapers, checkpoint.TotalEntities, checkpoint.NewInfoRatio, checkpoint.ShouldContinue);

        return checkpoint;
    }

    private async Task RunLlmSentinelAsync(
        SentinelCheckpoint checkpoint,
        UltraResearchState state,
        Dictionary<string, List<string>> topEntities,
        Dictionary<int, int> yearDistribution,
        IReadOnlyList<OrphanCitation> orphans,
        IReadOnlyList<FoundationalReference> foundational,
        CancellationToken ct)
    {
        try
        {
            var prompt = BuildSentinelPrompt(state, topEntities, yearDistribution, orphans, foundational);

            var json = await _ollama!.SentinelGenerateJsonAsync(prompt, ct: ct);

            var result = JsonSerializer.Deserialize<SentinelLlmResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result != null)
            {
                // Keep structurally computed NewInfoRatio for convergence detection —
                // LLM may hallucinate this value. LLM provides gap analysis + queries only.
                checkpoint.IdentifiedGaps = result.IdentifiedGaps ?? [];
                checkpoint.SuggestedQueries = result.SuggestedQueries ?? [];
                checkpoint.ShouldContinue = result.ShouldContinue;
                checkpoint.SentinelAnalysis = result.Reasoning;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM sentinel failed, falling back to structural mode");
            RunStructuralSentinel(checkpoint, state, orphans, yearDistribution);
        }
    }

    private void RunStructuralSentinel(
        SentinelCheckpoint checkpoint,
        UltraResearchState state,
        IReadOnlyList<OrphanCitation> orphans,
        Dictionary<int, int> yearDistribution)
    {
        var gaps = new List<string>();
        var queries = new List<string>();

        // Gap: orphan citations cited by 2+ docs
        var highPriorityOrphans = orphans.Where(o => o.CitingDocumentCount >= 2).ToList();
        if (highPriorityOrphans.Count > 5)
            gaps.Add($"{highPriorityOrphans.Count} references cited by 2+ papers are not yet ingested");

        // Gap: temporal gaps
        if (yearDistribution.Count > 0)
        {
            var years = yearDistribution.Keys.OrderBy(y => y).ToList();
            for (int i = 1; i < years.Count; i++)
            {
                if (years[i] - years[i - 1] > 2)
                    gaps.Add($"No papers between {years[i - 1]} and {years[i]}");
            }
        }

        // Suggest queries from orphan titles
        foreach (var orphan in orphans.Take(3))
        {
            if (!string.IsNullOrEmpty(orphan.Description))
                queries.Add(orphan.Description);
        }

        checkpoint.IdentifiedGaps = gaps;
        checkpoint.SuggestedQueries = queries;
        checkpoint.SentinelAnalysis = "Structural-only evaluation (no LLM available)";

        // Continue unless convergence signals are strong
        var orphanRatio = state.PapersIngested > 0
            ? (double)orphans.Count / state.PapersIngested
            : 1.0;
        checkpoint.ShouldContinue = checkpoint.NewInfoRatio >= state.Checkpoints.Count switch
        {
            >= 3 => 0.05, // After 3 checkpoints, allow lower threshold
            _ => 0.01
        } || orphanRatio > 0.10;
    }

    private string BuildSentinelPrompt(
        UltraResearchState state,
        Dictionary<string, List<string>> topEntities,
        Dictionary<int, int> yearDistribution,
        IReadOnlyList<OrphanCitation> orphans,
        IReadOnlyList<FoundationalReference> foundational)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are evaluating a research corpus on: \"{state.Topic}\"");
        sb.AppendLine($"Papers ingested: {state.PapersIngested}, Iteration: {state.Iteration}");
        sb.AppendLine();

        // Entity clusters
        sb.AppendLine("## Top Entities by Type");
        foreach (var (type, entities) in topEntities)
        {
            sb.AppendLine($"- {type}: {string.Join(", ", entities.Take(10))}");
        }
        sb.AppendLine();

        // Citation graph
        sb.AppendLine("## Citation Graph");
        sb.AppendLine($"- Orphan citations (unresolved): {orphans.Count}");
        sb.AppendLine($"- Top orphans: {string.Join(", ", orphans.Take(5).Select(o => o.CanonicalName))}");
        sb.AppendLine($"- Foundational refs: {string.Join(", ", foundational.Take(5).Select(f => f.CanonicalName))}");
        sb.AppendLine();

        // Year distribution
        sb.AppendLine("## Year Distribution");
        foreach (var (year, count) in yearDistribution.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"- {year}: {count} papers");
        }
        sb.AppendLine();

        // Search history
        sb.AppendLine("## Queries Already Searched");
        foreach (var query in state.SearchQueriesUsed.Take(20))
        {
            sb.AppendLine($"- {query}");
        }
        sb.AppendLine();

        // Recent convergence
        if (state.Checkpoints.Count > 0)
        {
            sb.AppendLine("## Recent Convergence Metrics");
            foreach (var cp in state.Checkpoints.TakeLast(3))
            {
                sb.AppendLine($"- Iteration {cp.Iteration}: papers={cp.TotalPapers}, entities={cp.TotalEntities}, newInfoRatio={cp.NewInfoRatio:F3}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Respond with JSON: {\"newInfoRatio\": <float>, \"identifiedGaps\": [<strings>], \"suggestedQueries\": [<strings>], \"shouldContinue\": <bool>, \"reasoning\": \"<text>\"}");

        return sb.ToString();
    }

    private async Task<Dictionary<string, List<string>>> GetTopEntitiesByTypeAsync(
        Guid collectionId, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>();

        var entities = await _db.DocumentEntityLinks
            .Where(del => _db.Documents
                .Where(d => d.CollectionId == collectionId)
                .Select(d => d.Id)
                .Contains(del.DocumentId))
            .Join(_db.Entities, del => del.EntityId, e => e.Id, (del, e) => e)
            .GroupBy(e => new { e.EntityType, e.CanonicalName })
            .Select(g => new { g.Key.EntityType, g.Key.CanonicalName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(100)
            .ToListAsync(ct);

        foreach (var entity in entities)
        {
            if (!result.ContainsKey(entity.EntityType))
                result[entity.EntityType] = [];

            if (result[entity.EntityType].Count < 20)
                result[entity.EntityType].Add(entity.CanonicalName);
        }

        return result;
    }

    private async Task<Dictionary<int, int>> GetYearDistributionAsync(
        Guid collectionId, CancellationToken ct)
    {
        var docs = await _db.Documents
            .Where(d => d.CollectionId == collectionId && d.SourceCreatedAt != null)
            .Select(d => d.SourceCreatedAt!.Value.Year)
            .ToListAsync(ct);

        return docs.GroupBy(y => y).ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task<bool> CheckLlmAvailableAsync()
    {
        try
        {
            return _ollama != null && await _ollama.IsAvailableAsync();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Internal response model for LLM sentinel JSON output.</summary>
    internal class SentinelLlmResponse
    {
        public double NewInfoRatio { get; set; }
        public List<string>? IdentifiedGaps { get; set; }
        public List<string>? SuggestedQueries { get; set; }
        public bool ShouldContinue { get; set; } = true;
        public string? Reasoning { get; set; }
    }
}

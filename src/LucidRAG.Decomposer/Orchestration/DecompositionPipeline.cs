using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Refinement;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace LucidRAG.Decomposer.Orchestration;

/// <summary>
/// Main entry point for query decomposition.
/// Orchestrates: Classify → Analyze → Refine → Plan.
///
/// Fast path: simple queries skip decomposition entirely.
/// The decomposer produces an enriched DecompositionResult that feeds
/// back into the sentinel LLM call and retrieval pipeline with
/// pre-computed embeddings, entity splits, and KB probe results.
/// </summary>
public class DecompositionPipeline
{
    private readonly ComplexityClassifier _complexityClassifier;
    private readonly ConceptClassifier _conceptClassifier;
    private readonly IReadOnlyList<IQueryAnalyzer> _analyzers;
    private readonly IDecompositionRefiner _refiner;
    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<DecompositionPipeline>? _logger;

    public DecompositionPipeline(
        ComplexityClassifier complexityClassifier,
        ConceptClassifier conceptClassifier,
        IEnumerable<IQueryAnalyzer> analyzers,
        IDecompositionRefiner refiner,
        IEmbeddingService? embedding = null,
        ILogger<DecompositionPipeline>? logger = null)
    {
        _complexityClassifier = complexityClassifier;
        _conceptClassifier = conceptClassifier;
        _analyzers = analyzers.ToList();
        _refiner = refiner;
        _embedding = embedding;
        _logger = logger;
    }

    /// <summary>
    /// Decompose a query. This is the main entry point.
    /// </summary>
    /// <param name="query">Raw user query.</param>
    /// <param name="entities">NER entities (from QueryPreprocessor).</param>
    /// <param name="hasUrls">Whether query contains URLs (from RecognizedSignals).</param>
    /// <param name="hasDateTimes">Whether query contains date/time expressions.</param>
    /// <param name="sentinelData">LLM sentinel output (SentinelRefinementInput).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DecompositionResult> DecomposeAsync(
        string query,
        List<NerEntity>? entities = null,
        bool hasUrls = false,
        bool hasDateTimes = false,
        object? sentinelData = null,
        CancellationToken ct = default)
    {
        entities ??= [];

        // ─── STEP 1: Fast-path classification ───
        var entityTypeCount = entities.Select(e => e.Type).Distinct().Count();
        var complexity = await _complexityClassifier.ClassifyAsync(
            query, entities.Count, entityTypeCount, hasUrls, hasDateTimes, ct);

        _logger?.LogDebug("Query complexity: {Complexity} for: {Query}", complexity, query);

        // ─── STEP 2: Concept classification ───
        var embeddingCache = new Dictionary<string, float[]>();
        var (concept, archetypeScores) = await _conceptClassifier.ClassifyAsync(
            query, embeddingCache, ct);

        _logger?.LogDebug("Concept: {Concept} for: {Query}", concept, query);

        // Pre-embed the query for downstream use
        float[]? queryEmbedding = null;
        if (_embedding != null)
        {
            if (!embeddingCache.TryGetValue(query, out queryEmbedding))
            {
                queryEmbedding = await _embedding.EmbedAsync(query, ct);
                embeddingCache[query] = queryEmbedding;
            }
        }

        // ─── FAST PATH: Simple queries skip decomposition ───
        if (complexity == QueryComplexity.Simple)
        {
            _logger?.LogDebug("Fast path: skipping decomposition for simple query");

            var signals = new QuerySignals
            {
                OriginalQuery = query,
                Entities = entities,
                DetectedConcept = concept,
                Complexity = complexity,
                QueryEmbedding = queryEmbedding,
                ArchetypeScores = archetypeScores,
                EmbeddingCache = embeddingCache
            };

            // Still run through refiner to apply sentinel enhancements
            return _refiner.Refine(signals, sentinelData);
        }

        // ─── STEP 3: Phase 1 — Deterministic Analysis ───
        var currentSignals = new QuerySignals
        {
            OriginalQuery = query,
            Entities = entities,
            DetectedConcept = concept,
            Complexity = complexity,
            QueryEmbedding = queryEmbedding,
            ArchetypeScores = archetypeScores,
            EmbeddingCache = embeddingCache
        };

        foreach (var analyzer in _analyzers)
        {
            ct.ThrowIfCancellationRequested();
            currentSignals = await analyzer.AnalyzeAsync(query, currentSignals, ct);
        }

        _logger?.LogDebug("Phase 1 complete: {NodeCount} proposed nodes, {RefCount} references",
            currentSignals.ProposedNodes.Count, currentSignals.References.Count);

        // ─── STEP 4: Phase 2 — LLM Refinement ───
        var result = _refiner.Refine(currentSignals, sentinelData);

        _logger?.LogDebug("Decomposition complete: {NodeCount} final nodes, complexity={Complexity}, concept={Concept}",
            result.Nodes.Count, result.Complexity, result.Concept);

        return result;
    }
}

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
        var embeddingCache = new Dictionary<string, float[]>();

        // ─── STEP 1: Fast-path classification ───
        var entityTypeCount = entities.Select(e => e.Type).Distinct().Count();
        var complexity = await _complexityClassifier.ClassifyAsync(
            query, entities.Count, entityTypeCount, hasUrls, hasDateTimes, embeddingCache, ct);

        _logger?.LogDebug("Query complexity: {Complexity} for: {Query}", complexity, query);

        // ─── STEP 2: Concept classification ───
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

        // Split analyzers into independent (parallel) and dependent (sequential) groups.
        // Phase 1 analyzers read from input signals only and don't depend on each other's output.
        // Phase 2 analyzers check accumulated state (ProposedNodes.Count, DetectedTools.Count).
        var phase1 = new List<IQueryAnalyzer>();
        var phase2 = new List<IQueryAnalyzer>();

        foreach (var analyzer in _analyzers)
        {
            if (analyzer is SemanticClusterAnalyzer or ToolUseAnalyzer)
                phase2.Add(analyzer);
            else
                phase1.Add(analyzer);
        }

        // Phase 1: run independent analyzers in parallel
        if (phase1.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var phase1Tasks = phase1.Select(a => a.AnalyzeAsync(query, currentSignals, ct)).ToArray();
            var phase1Results = await Task.WhenAll(phase1Tasks);
            currentSignals = MergeSignals(currentSignals, phase1Results);
        }

        // Phase 2: run dependent analyzers sequentially
        foreach (var analyzer in phase2)
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

    /// <summary>
    /// Merge signals from parallel analyzer results back into a single QuerySignals.
    /// Unions ProposedNodes, References, DetectedTools, and EmbeddingCache from all results.
    /// </summary>
    private static QuerySignals MergeSignals(QuerySignals baseline, QuerySignals[] results)
    {
        var mergedNodes = new List<QueryNode>(baseline.ProposedNodes);
        var mergedRefs = new List<ContentReference>(baseline.References);
        var mergedTools = new List<ToolAction>(baseline.DetectedTools);
        var mergedCache = new Dictionary<string, float[]>(baseline.EmbeddingCache);
        var mergedScores = new Dictionary<string, float>(baseline.ArchetypeScores);
        var mergedComplexity = baseline.Complexity;

        foreach (var result in results)
        {
            // Union new nodes (skip duplicates already in baseline)
            foreach (var node in result.ProposedNodes)
            {
                if (!mergedNodes.Any(n => n.Id == node.Id))
                    mergedNodes.Add(node);
            }

            // Union references
            foreach (var r in result.References)
            {
                if (!mergedRefs.Any(existing => existing.Uri == r.Uri))
                    mergedRefs.Add(r);
            }

            // Union tools
            mergedTools.AddRange(result.DetectedTools.Where(t =>
                !mergedTools.Any(existing => existing.Intent == t.Intent)));

            // Merge embedding cache
            foreach (var (key, value) in result.EmbeddingCache)
                mergedCache.TryAdd(key, value);

            // Merge archetype scores (take max)
            foreach (var (key, value) in result.ArchetypeScores)
            {
                if (!mergedScores.TryGetValue(key, out var existing) || value > existing)
                    mergedScores[key] = value;
            }

            // Take highest complexity
            if (result.Complexity > mergedComplexity)
                mergedComplexity = result.Complexity;
        }

        return baseline with
        {
            ProposedNodes = mergedNodes,
            References = mergedRefs,
            DetectedTools = mergedTools,
            EmbeddingCache = mergedCache,
            ArchetypeScores = mergedScores,
            Complexity = mergedComplexity
        };
    }
}

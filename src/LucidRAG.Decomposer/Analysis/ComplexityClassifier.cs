using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
///     Fast-path classifier: determines query complexity BEFORE running expensive analyzers.
///     Simple queries skip decomposition entirely and go straight to single-pass fetch+score.
///     This is the first thing that runs  -  it gates whether we do any decomposition at all.
///     Classification signals (all deterministic, no LLM):
///     - Sentence count (splitting on . ; \n and conjunctions with topic-change detection)
///     - URL/file path presence (regex-free: uses TextRecognizerService)
///     - NER entity count + type diversity
///     - Temporal comparison markers
///     - Archetype similarity (comparison, timeline archetypes via embedding)
/// </summary>
public class ComplexityClassifier
{
    // Pre-embedded archetype texts (embedded lazily at first use)
    private static readonly string[] ComparisonArchetypes =
    [
        "How does X compare to Y?",
        "What is the difference between X and Y?",
        "X versus Y pros and cons",
        "Compare and contrast X with Y"
    ];

    private static readonly string[] TemporalComparisonArchetypes =
    [
        "How has X changed over time?",
        "What happened with X before and after 2024?",
        "Evolution of X from past to present",
        "X trends over the past year"
    ];

    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<ComplexityClassifier>? _logger;

    private float[][]? _comparisonArchetypeEmbeddings;
    private float[][]? _temporalArchetypeEmbeddings;

    public ComplexityClassifier(IEmbeddingService? embedding = null, ILogger<ComplexityClassifier>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    /// <summary>
    ///     Classify query complexity. This is the fast-path gate.
    /// </summary>
    public async Task<QueryComplexity> ClassifyAsync(
        string query,
        int entityCount,
        int entityTypeCount,
        bool hasUrls,
        bool hasDateTimes,
        Dictionary<string, float[]>? embeddingCache = null,
        CancellationToken ct = default)
    {
        // Obvious complex signals (no embedding needed)
        if (hasUrls) return QueryComplexity.Moderate;
        if (entityCount >= 3) return QueryComplexity.Complex;
        if (entityTypeCount >= 2 && entityCount >= 2) return QueryComplexity.Moderate;

        // Tool-use signals: file paths + tool verbs → at least Moderate
        if (HasToolUseSignals(query))
        {
            _logger?.LogDebug("Tool-use signals detected, upgrading to Moderate: {Query}", query);
            return QueryComplexity.Moderate;
        }

        // Check for multiple sentences/clauses
        var clauseCount = CountClauses(query);
        if (clauseCount >= 3) return QueryComplexity.Complex;

        // If we have embedding service, check archetype similarity
        if (_embedding != null && clauseCount >= 2)
        {
            await EnsureArchetypeEmbeddingsAsync(ct);

            embeddingCache ??= new Dictionary<string, float[]>();
            if (!embeddingCache.TryGetValue(query, out var queryEmbedding))
            {
                queryEmbedding = await _embedding.EmbedAsync(query, ct);
                embeddingCache[query] = queryEmbedding;
            }

            var comparisonScore = MaxSimilarity(queryEmbedding, _comparisonArchetypeEmbeddings!);
            if (comparisonScore >= 0.55f)
            {
                _logger?.LogDebug("Query classified as Comparison (score={Score:F2}): {Query}",
                    comparisonScore, query);
                return QueryComplexity.Complex;
            }

            if (hasDateTimes)
            {
                var temporalScore = MaxSimilarity(queryEmbedding, _temporalArchetypeEmbeddings!);
                if (temporalScore >= 0.50f)
                {
                    _logger?.LogDebug("Query classified as TemporalComparison (score={Score:F2}): {Query}",
                        temporalScore, query);
                    return QueryComplexity.Complex;
                }
            }
        }

        // Two clauses without strong archetype match = moderate
        if (clauseCount == 2) return QueryComplexity.Moderate;

        return QueryComplexity.Simple;
    }

    /// <summary>
    ///     Detect tool-use signals: file paths + tool action verbs.
    ///     These should prevent fast-path since they need ToolUseAnalyzer.
    /// </summary>
    internal static bool HasToolUseSignals(string query)
    {
        // File path patterns (Windows C:\, Unix /home/, ~/)  -  case-insensitive, zero-allocation
        var hasFilePath = query.Contains("c:/", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("c:\\", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("d:/", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("d:\\", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("/home/", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("~/", StringComparison.OrdinalIgnoreCase);

        // Tool action verbs
        var hasToolVerb = query.Contains("index ", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("ingest ", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("crawl ", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("spider ", StringComparison.OrdinalIgnoreCase) ||
                          query.Contains("scrape ", StringComparison.OrdinalIgnoreCase) ||
                          (query.Contains("build", StringComparison.OrdinalIgnoreCase) &&
                           (query.Contains("knowledge", StringComparison.OrdinalIgnoreCase) ||
                            query.Contains(" kb ", StringComparison.OrdinalIgnoreCase))) ||
                          (query.Contains("create", StringComparison.OrdinalIgnoreCase) &&
                           (query.Contains("knowledge", StringComparison.OrdinalIgnoreCase) ||
                            query.Contains(" kb ", StringComparison.OrdinalIgnoreCase)));

        return hasFilePath || hasToolVerb;
    }

    /// <summary>
    ///     Count syntactic clauses (sentence boundaries + conjunctions that likely split topics).
    /// </summary>
    internal static int CountClauses(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        var count = 1;
        var span = query.AsSpan();

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            // Sentence boundaries
            if (c is '.' or ';' or '\n')
            {
                // Check there's non-whitespace after
                var rest = span[(i + 1)..].TrimStart();
                if (rest.Length > 3) count++;
            }
        }

        // Check for coordinating conjunctions that likely split topics  -  zero-allocation
        if (query.Contains(" and also ", StringComparison.OrdinalIgnoreCase)) count++;
        if (query.Contains(" as well as ", StringComparison.OrdinalIgnoreCase)) count++;
        if (query.Contains(" plus ", StringComparison.OrdinalIgnoreCase)) count++;
        if (query.Contains(" in addition ", StringComparison.OrdinalIgnoreCase)) count++;
        // Simple "and" only counts if query is long enough to have two topics
        if (query.Contains(" and ", StringComparison.OrdinalIgnoreCase) && query.Length > 40)
        {
            // Only count if the "and" isn't near the start (not "find and show me")
            var andIdx = query.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
            if (andIdx > 15) count++;
        }

        return count;
    }

    private async Task EnsureArchetypeEmbeddingsAsync(CancellationToken ct)
    {
        if (_comparisonArchetypeEmbeddings != null) return;

        // Flatten comparison + temporal archetypes into a single batch call.
        var allTexts = new List<string>(ComparisonArchetypes);
        allTexts.AddRange(TemporalComparisonArchetypes);

        var allEmbeddings = await _embedding!.EmbedBatchAsync(allTexts, ct);

        _comparisonArchetypeEmbeddings = allEmbeddings[..ComparisonArchetypes.Length];
        _temporalArchetypeEmbeddings = allEmbeddings[ComparisonArchetypes.Length..];
    }

    private static float MaxSimilarity(float[] query, float[][] archetypes)
    {
        var max = 0f;
        foreach (var archetype in archetypes)
        {
            var sim = CosineSimilarity(query, archetype);
            if (sim > max) max = sim;
        }

        return max;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        return TensorPrimitives.CosineSimilarity(a, b);
    }
}
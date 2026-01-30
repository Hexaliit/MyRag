using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
/// Fast-path classifier: determines query complexity BEFORE running expensive analyzers.
/// Simple queries skip decomposition entirely and go straight to single-pass fetch+score.
/// This is the first thing that runs — it gates whether we do any decomposition at all.
///
/// Classification signals (all deterministic, no LLM):
/// - Sentence count (splitting on . ; \n and conjunctions with topic-change detection)
/// - URL/file path presence (regex-free: uses TextRecognizerService)
/// - NER entity count + type diversity
/// - Temporal comparison markers
/// - Archetype similarity (comparison, timeline archetypes via embedding)
/// </summary>
public class ComplexityClassifier
{
    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<ComplexityClassifier>? _logger;

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

    private float[][]? _comparisonArchetypeEmbeddings;
    private float[][]? _temporalArchetypeEmbeddings;

    public ComplexityClassifier(IEmbeddingService? embedding = null, ILogger<ComplexityClassifier>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    /// <summary>
    /// Classify query complexity. This is the fast-path gate.
    /// </summary>
    public async Task<QueryComplexity> ClassifyAsync(
        string query,
        int entityCount,
        int entityTypeCount,
        bool hasUrls,
        bool hasDateTimes,
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

            var queryEmbedding = await _embedding.EmbedAsync(query, ct);

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
    /// Detect tool-use signals: file paths + tool action verbs.
    /// These should prevent fast-path since they need ToolUseAnalyzer.
    /// </summary>
    internal static bool HasToolUseSignals(string query)
    {
        var lower = query.ToLowerInvariant();

        // File path patterns (Windows C:\, Unix /home/, ~/)
        var hasFilePath = (lower.Contains("c:/") || lower.Contains("c:\\") ||
                           lower.Contains("d:/") || lower.Contains("d:\\") ||
                           lower.Contains("/home/") || lower.Contains("~/"));

        // Tool action verbs
        var hasToolVerb = lower.Contains("index ") || lower.Contains("ingest ") ||
                          lower.Contains("crawl ") || lower.Contains("spider ") ||
                          lower.Contains("scrape ") ||
                          (lower.Contains("build") && (lower.Contains("knowledge") || lower.Contains(" kb "))) ||
                          (lower.Contains("create") && (lower.Contains("knowledge") || lower.Contains(" kb ")));

        return hasFilePath || hasToolVerb;
    }

    /// <summary>
    /// Count syntactic clauses (sentence boundaries + conjunctions that likely split topics).
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

        // Check for coordinating conjunctions that likely split topics
        // "and also", "as well as", "plus", "in addition"
        var lower = query.ToLowerInvariant();
        if (lower.Contains(" and also ")) count++;
        if (lower.Contains(" as well as ")) count++;
        if (lower.Contains(" plus ")) count++;
        if (lower.Contains(" in addition ")) count++;
        // Simple "and" only counts if query is long enough to have two topics
        if (lower.Contains(" and ") && query.Length > 40)
        {
            // Only count if the "and" isn't near the start (not "find and show me")
            var andIdx = lower.IndexOf(" and ", StringComparison.Ordinal);
            if (andIdx > 15) count++;
        }

        return count;
    }

    private async Task EnsureArchetypeEmbeddingsAsync(CancellationToken ct)
    {
        if (_comparisonArchetypeEmbeddings != null) return;

        _comparisonArchetypeEmbeddings = await _embedding!.EmbedBatchAsync(ComparisonArchetypes, ct);
        _temporalArchetypeEmbeddings = await _embedding.EmbedBatchAsync(TemporalComparisonArchetypes, ct);
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

    internal static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }
}

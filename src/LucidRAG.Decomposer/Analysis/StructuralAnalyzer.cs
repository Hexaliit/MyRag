using System.Text;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
///     Splits compound queries using syntactic structure + embedding-based topic boundary detection.
///     Instead of hardcoded conjunction lists, uses cosine similarity to detect topical boundaries:
///     - Embed the clause before and after a conjunction
///     - If cosine similarity &lt; 0.40 → different topics → split
///     - If cosine similarity &gt;= 0.40 → same topic → keep together
/// </summary>
public class StructuralAnalyzer : IQueryAnalyzer
{
    /// <summary>Cosine threshold below which two clauses are considered different topics.</summary>
    private const float TopicBoundaryThreshold = 0.40f;

    /// <summary>Conjunctions that might split topics (checked via embedding, not assumed).</summary>
    private static readonly string[] Conjunctions =
        ["and", "also", "plus", "as well as", "in addition to", "along with", "but also"];

    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<StructuralAnalyzer>? _logger;

    public StructuralAnalyzer(IEmbeddingService? embedding = null, ILogger<StructuralAnalyzer>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<QuerySignals> AnalyzeAsync(string query, QuerySignals existing, CancellationToken ct = default)
    {
        var nodes = new List<QueryNode>(existing.ProposedNodes);
        var embeddingCache = new Dictionary<string, float[]>(existing.EmbeddingCache);

        // Phase 1: Split on hard boundaries (sentence separators)
        var hardClauses = SplitOnHardBoundaries(query);

        // Phase 2: For each hard clause, check for conjunction-based topic splits
        var allClauses = new List<string>();
        foreach (var clause in hardClauses)
            if (_embedding != null)
            {
                var subClauses = await SplitOnConjunctionsAsync(clause, embeddingCache, ct);
                allClauses.AddRange(subClauses);
            }
            else
            {
                allClauses.Add(clause);
            }

        // Only decompose if we found multiple clauses
        if (allClauses.Count > 1)
        {
            foreach (var clause in allClauses)
            {
                var trimmed = clause.Trim();
                if (trimmed.Length < 5) continue; // Skip fragments

                float[]? embedding = null;
                if (_embedding != null && !embeddingCache.TryGetValue(trimmed, out embedding))
                {
                    embedding = await _embedding.EmbedAsync(trimmed, ct);
                    embeddingCache[trimmed] = embedding;
                }

                nodes.Add(new QueryNode
                {
                    Query = trimmed,
                    Type = QueryNodeType.Atomic,
                    Embedding = embedding,
                    Depth = 0
                });
            }

            _logger?.LogDebug("Structural split: {Count} clauses from query: {Query}",
                allClauses.Count, query);
        }

        return existing with
        {
            ProposedNodes = nodes,
            EmbeddingCache = embeddingCache
        };
    }

    /// <summary>
    ///     Split on hard sentence boundaries: period, semicolon, newline.
    ///     Only splits if there's substantial text on both sides.
    /// </summary>
    internal static List<string> SplitOnHardBoundaries(string query)
    {
        var result = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < query.Length; i++)
        {
            var c = query[i];
            if (c is '.' or ';' or '\n')
            {
                var clause = current.ToString().Trim();
                if (clause.Length >= 5) result.Add(clause);
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        var last = current.ToString().Trim();
        if (last.Length >= 5) result.Add(last);

        // If only one clause, return original
        return result.Count > 1 ? result : [query.Trim()];
    }

    /// <summary>
    ///     Check conjunctions within a clause. Split only if embedding similarity
    ///     between the parts is below the topic boundary threshold.
    /// </summary>
    private async Task<List<string>> SplitOnConjunctionsAsync(
        string clause,
        Dictionary<string, float[]> embeddingCache,
        CancellationToken ct)
    {
        var lower = clause.ToLowerInvariant();

        foreach (var conj in Conjunctions)
        {
            var pattern = $" {conj} ";
            var idx = lower.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) continue;

            var before = clause[..idx].Trim();
            var after = clause[(idx + pattern.Length)..].Trim();

            if (before.Length < 5 || after.Length < 5) continue;

            // Embed both sides (batch when both are cache misses)
            var hasBefore = embeddingCache.TryGetValue(before, out var embBefore);
            var hasAfter = embeddingCache.TryGetValue(after, out var embAfter);

            if (!hasBefore && !hasAfter)
            {
                // Both miss: single batch call instead of two sequential calls
                var batch = await _embedding!.EmbedBatchAsync([before, after], ct);
                embBefore = batch[0];
                embAfter = batch[1];
                embeddingCache[before] = embBefore;
                embeddingCache[after] = embAfter;
            }
            else
            {
                if (!hasBefore)
                {
                    embBefore = await _embedding!.EmbedAsync(before, ct);
                    embeddingCache[before] = embBefore;
                }

                if (!hasAfter)
                {
                    embAfter = await _embedding!.EmbedAsync(after, ct);
                    embeddingCache[after] = embAfter;
                }
            }

            var similarity = ComplexityClassifier.CosineSimilarity(embBefore, embAfter);

            _logger?.LogDebug(
                "Conjunction '{Conj}' similarity={Sim:F3}: [{Before}] vs [{After}]",
                conj, similarity, before, after);

            if (similarity < TopicBoundaryThreshold)
                // Different topics  -  split
                return [before, after];

            // Same topic  -  keep together, don't check further conjunctions
            break;
        }

        return [clause];
    }
}
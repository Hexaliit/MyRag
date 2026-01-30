using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
/// Uses NER entities to detect multi-entity queries that need separate treatment.
/// When a query mentions 2+ distinct entities of the same type (e.g., 2 ORGs),
/// checks if the query implies comparison via embedding archetype matching.
/// </summary>
public class EntityRelationAnalyzer : IQueryAnalyzer
{
    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<EntityRelationAnalyzer>? _logger;

    private float[][]? _comparisonArchetypes;

    private static readonly string[] ComparisonArchetypeTexts =
    [
        "How does X compare to Y?",
        "Difference between X and Y",
        "X versus Y",
        "Pros and cons of X vs Y",
        "Which is better, X or Y?"
    ];

    public EntityRelationAnalyzer(IEmbeddingService? embedding = null, ILogger<EntityRelationAnalyzer>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<QuerySignals> AnalyzeAsync(string query, QuerySignals existing, CancellationToken ct = default)
    {
        var entities = existing.Entities;
        if (entities.Count < 2) return existing;

        var nodes = new List<QueryNode>(existing.ProposedNodes);
        var archetypeScores = new Dictionary<string, float>(existing.ArchetypeScores);
        var embeddingCache = new Dictionary<string, float[]>(existing.EmbeddingCache);

        // Group entities by type
        var byType = entities.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.ToList());

        // Check for same-type entity pairs (potential comparisons)
        foreach (var (type, typeEntities) in byType)
        {
            if (typeEntities.Count < 2) continue;

            // Check if query implies comparison via archetype matching
            if (_embedding != null)
            {
                var comparisonScore = await GetComparisonScoreAsync(query, embeddingCache, ct);
                archetypeScores["Comparison"] = comparisonScore;

                if (comparisonScore >= 0.50f)
                {
                    _logger?.LogDebug(
                        "Comparison detected (score={Score:F2}) between {Count} {Type} entities: {Entities}",
                        comparisonScore, typeEntities.Count, type,
                        string.Join(", ", typeEntities.Select(e => e.Text)));

                    // Create comparison node with entity-specific sub-queries
                    var comparisonNode = new QueryNode
                    {
                        Query = query,
                        Type = QueryNodeType.Comparison,
                        Concept = ConceptType.Comparison,
                        Depth = 0,
                        Children = typeEntities.Select(entity => new QueryNode
                        {
                            Query = $"{query} focusing on {entity.Text}",
                            Type = QueryNodeType.Atomic,
                            RelevantEntities = [entity],
                            Depth = 1
                        }).ToList()
                    };

                    nodes.Add(comparisonNode);
                }
            }
        }

        // Check for different-type entities (may need separate scoping)
        if (byType.Count >= 2 && entities.Count >= 3)
        {
            _logger?.LogDebug("Multi-type entities detected: {Types}",
                string.Join(", ", byType.Keys));
            // Entity-scoped sub-queries for better source routing
            // (only if not already decomposed as comparison)
            if (!nodes.Any(n => n.Type == QueryNodeType.Comparison))
            {
                foreach (var entity in entities.Take(4))
                {
                    nodes.Add(new QueryNode
                    {
                        Query = query,
                        Type = QueryNodeType.Atomic,
                        RelevantEntities = [entity],
                        Depth = 0
                    });
                }
            }
        }

        return existing with
        {
            ProposedNodes = nodes,
            ArchetypeScores = archetypeScores,
            EmbeddingCache = embeddingCache
        };
    }

    private async Task<float> GetComparisonScoreAsync(
        string query,
        Dictionary<string, float[]> embeddingCache,
        CancellationToken ct)
    {
        if (_comparisonArchetypes == null)
            _comparisonArchetypes = await _embedding!.EmbedBatchAsync(ComparisonArchetypeTexts, ct);

        if (!embeddingCache.TryGetValue(query, out var queryEmb))
        {
            queryEmb = await _embedding!.EmbedAsync(query, ct);
            embeddingCache[query] = queryEmb;
        }

        var max = 0f;
        foreach (var archetype in _comparisonArchetypes)
        {
            var sim = ComplexityClassifier.CosineSimilarity(queryEmb, archetype);
            if (sim > max) max = sim;
        }
        return max;
    }
}

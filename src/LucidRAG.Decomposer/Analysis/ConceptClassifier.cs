using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
/// Classifies query concept type using embedding archetype matching.
/// Replaces regex word-list detection with semantic similarity to pre-embedded archetypes.
/// The concept type drives both retrieval strategy and response shape.
/// </summary>
public class ConceptClassifier
{
    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<ConceptClassifier>? _logger;

    private Dictionary<ConceptType, float[][]>? _archetypeEmbeddings;

    /// <summary>Archetype texts for each concept type.</summary>
    private static readonly Dictionary<ConceptType, string[]> Archetypes = new()
    {
        [ConceptType.Procedure] =
        [
            "How do I configure X?",
            "Steps to set up X",
            "Guide to installing X",
            "Tutorial for building X",
            "How to fix X step by step"
        ],
        [ConceptType.Definition] =
        [
            "What is X?",
            "Define X",
            "Meaning of X",
            "Explain what X means",
            "What does X refer to?"
        ],
        [ConceptType.Comparison] =
        [
            "X versus Y",
            "Compare X and Y",
            "Difference between X and Y",
            "Pros and cons of X vs Y",
            "Which is better, X or Y?"
        ],
        [ConceptType.Timeline] =
        [
            "History of X",
            "How has X evolved over time?",
            "Timeline of X developments",
            "What happened with X since 2023?",
            "Evolution of X from past to present"
        ],
        [ConceptType.Troubleshooting] =
        [
            "Error when doing X",
            "Why does X fail?",
            "Fix for X not working",
            "X throws exception, how to resolve?",
            "Debugging X issue"
        ],
        [ConceptType.Incident] =
        [
            "What happened with the X outage?",
            "Status of the X incident",
            "X service is down, what's going on?",
            "Impact of the X breach",
            "X failure timeline"
        ],
        [ConceptType.Decision] =
        [
            "Should I use X or Y?",
            "Trade-offs of choosing X",
            "Is X worth it?",
            "Pros and cons of X",
            "Best option for X"
        ],
        [ConceptType.Architecture] =
        [
            "How does X system work?",
            "Architecture of X",
            "Components of the X system",
            "How are X and Y connected in the system?",
            "Design of the X pipeline"
        ],
        [ConceptType.Roundup] =
        [
            "Latest news about X",
            "What happened today in X?",
            "This week's X highlights",
            "Recent developments in X",
            "Top stories about X"
        ]
    };

    /// <summary>Minimum similarity score to assign a concept type.</summary>
    private const float MinConceptScore = 0.45f;

    public ConceptClassifier(IEmbeddingService? embedding = null, ILogger<ConceptClassifier>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    /// <summary>
    /// Classify the concept type of a query using embedding archetype matching.
    /// Returns the best matching concept type and all scores.
    /// </summary>
    public async Task<(ConceptType concept, Dictionary<string, float> scores)> ClassifyAsync(
        string query,
        Dictionary<string, float[]>? embeddingCache = null,
        CancellationToken ct = default)
    {
        if (_embedding == null)
            return (ConceptType.General, new Dictionary<string, float>());

        await EnsureArchetypeEmbeddingsAsync(ct);
        embeddingCache ??= new Dictionary<string, float[]>();

        if (!embeddingCache.TryGetValue(query, out var queryEmb))
        {
            queryEmb = await _embedding.EmbedAsync(query, ct);
            embeddingCache[query] = queryEmb;
        }

        var scores = new Dictionary<string, float>();
        var bestConcept = ConceptType.General;
        var bestScore = 0f;

        foreach (var (concept, archetypes) in _archetypeEmbeddings!)
        {
            var maxSim = 0f;
            foreach (var archetype in archetypes)
            {
                var sim = ComplexityClassifier.CosineSimilarity(queryEmb, archetype);
                if (sim > maxSim) maxSim = sim;
            }

            scores[concept.ToString()] = maxSim;

            if (maxSim > bestScore && maxSim >= MinConceptScore)
            {
                bestScore = maxSim;
                bestConcept = concept;
            }
        }

        _logger?.LogDebug("Concept classification: {Concept} (score={Score:F2}) for: {Query}",
            bestConcept, bestScore, query);

        return (bestConcept, scores);
    }

    private async Task EnsureArchetypeEmbeddingsAsync(CancellationToken ct)
    {
        if (_archetypeEmbeddings != null) return;

        _archetypeEmbeddings = new Dictionary<ConceptType, float[][]>();
        foreach (var (concept, texts) in Archetypes)
        {
            _archetypeEmbeddings[concept] = await _embedding!.EmbedBatchAsync(texts, ct);
        }
    }
}

using System.Collections.Concurrent;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
///     Embedding-based query expansion for BM25 recall improvement.
///     Uses embedding similarity to find synonyms from the actual corpus vocabulary.
///     "pharmaceutical" → ["pharmaceutical", "drug", "medicine", "treatment"]
///     "AI" → ["AI", "artificial", "intelligence", "machine", "learning"]
///     BM25 on expanded terms gives semantic-ish matching at BM25 speed,
///     bridging the vocabulary mismatch gap without requiring full embedding search.
///     Vocabulary source (adaptive, in priority order):
///     1. High-IDF terms from the keyword corpus (real document terms)
///     2. Graceful fallback to empty expansion when corpus is new/empty
///     The vocabulary grows automatically as documents are ingested and the
///     keyword corpus is updated — no hardcoded domain lists needed.
/// </summary>
public sealed class QueryExpansionService
{
    /// <summary>Maximum number of corpus terms to use as expansion vocabulary.</summary>
    private const int MaxVocabularySize = 500;

    /// <summary>
    ///     Minimum document frequency for a term to be included in the vocabulary.
    ///     Terms appearing in fewer documents are too rare to be useful expansions.
    /// </summary>
    private const int MinDocumentFrequency = 2;

    /// <summary>
    ///     Maximum document frequency ratio. Terms appearing in more than 50% of docs
    ///     are too common to provide discriminative expansions.
    /// </summary>
    private const double MaxDocumentFrequencyRatio = 0.50;

    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new();
    private readonly IEmbeddingService _embedding;
    private readonly StorageService _storage;
    private readonly ConcurrentDictionary<string, float[]> _vocabEmbeddings = new();
    private bool _initialized;

    public QueryExpansionService(IEmbeddingService embedding, StorageService storage)
    {
        _embedding = embedding;
        _storage = storage;
    }

    /// <summary>
    ///     Expand a BM25 query by appending embedding-similar synonyms.
    ///     Returns the original query with expansion terms appended.
    /// </summary>
    public async Task<string> ExpandForBm25Async(
        string query,
        int maxExpansionsPerTerm = 3,
        double minSimilarity = 0.65,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        await EnsureInitializedAsync(ct);

        // No vocabulary available (empty corpus) — return original query
        if (_vocabEmbeddings.IsEmpty)
            return query;

        var terms = RelevanceScorer.Tokenize(query);
        var expansions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in terms)
        {
            if (ct.IsCancellationRequested) break;
            if (term.Length < 3 || StopwordLists.IsStopword(term))
                continue;

            var expanded = await ExpandTermAsync(term, maxExpansionsPerTerm, minSimilarity, ct);
            foreach (var exp in expanded)
                if (!string.Equals(exp, term, StringComparison.OrdinalIgnoreCase))
                    expansions.Add(exp);
        }

        if (expansions.Count == 0)
            return query;

        return $"{query} {string.Join(" ", expansions)}";
    }

    private async Task<IReadOnlyList<string>> ExpandTermAsync(
        string term,
        int maxExpansions,
        double minSimilarity,
        CancellationToken ct)
    {
        var key = term.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        float[] termEmbed;
        try
        {
            termEmbed = await _embedding.EmbedAsync(key, ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Query expansion embedding failed for '{key}': {ex.Message}");
            return [key];
        }

        var similar = new List<(string term, double sim)>();
        foreach (var (vocabTerm, vocabEmbed) in _vocabEmbeddings)
        {
            if (string.Equals(vocabTerm, key, StringComparison.OrdinalIgnoreCase))
                continue;
            var sim = VectorMath.CosineSimilarity(termEmbed, vocabEmbed);
            if (sim >= minSimilarity)
                similar.Add((vocabTerm, sim));
        }

        var result = similar
            .OrderByDescending(x => x.sim)
            .Take(maxExpansions)
            .Select(x => x.term)
            .Prepend(key)
            .ToList();

        _cache[key] = result;
        return result;
    }

    /// <summary>
    ///     Build expansion vocabulary from the actual corpus keyword_corpus table.
    ///     Selects terms with good IDF characteristics (not too rare, not too common).
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        try
        {
            var corpus = await _storage.GetKeywordCorpusAsync();
            if (corpus.Count == 0)
            {
                _initialized = true;
                return; // Empty corpus — expansion will be a no-op until documents are ingested
            }

            var corpusSize = await _storage.GetKeywordCorpusSizeAsync();
            var maxDf = (int)(corpusSize * MaxDocumentFrequencyRatio);

            // Select terms with good IDF: not too rare (minDF), not too common (maxDF ratio),
            // not stopwords, not too short. Sort by IDF descending (most discriminative first).
            var candidateTerms = corpus
                .Where(kv => kv.Value >= MinDocumentFrequency
                             && kv.Value <= maxDf
                             && kv.Key.Length >= 3
                             && !StopwordLists.IsStopword(kv.Key))
                .OrderBy(kv => kv.Value) // Low DF = high IDF = most discriminative
                .Take(MaxVocabularySize)
                .Select(kv => kv.Key)
                .ToList();

            if (candidateTerms.Count == 0)
            {
                _initialized = true;
                return;
            }

            // Batch-embed all vocabulary terms
            var embeddings = await _embedding.EmbedBatchAsync(candidateTerms, ct);
            for (var i = 0; i < candidateTerms.Count; i++)
                _vocabEmbeddings[candidateTerms[i].ToLowerInvariant()] = embeddings[i];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Query expansion vocab init failed: {ex.Message}");
        }

        _initialized = true;
    }
}
using System.Text.RegularExpressions;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Two-phase relevance scorer using Reciprocal Rank Fusion (RRF) across multiple signals.
/// Phase 1 (fast): BM25 + freshness + source authority — no embeddings needed, for early discard.
/// Phase 2 (full): adds query similarity + vibe alignment via embeddings for precise ranking.
/// </summary>
public class RelevanceScorer
{
    private static readonly Regex TokenRx = new(@"\b\w+\b", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
        "by", "from", "as", "is", "was", "are", "were", "been", "be", "have", "has", "had",
        "do", "does", "did", "will", "would", "could", "should", "may", "might", "must",
        "shall", "can", "this", "that", "these", "those", "it", "its", "he", "she", "they",
        "him", "her", "them", "his", "their", "my", "your", "our", "who", "which", "what",
        "when", "where", "why", "how", "all", "each", "every", "both", "few", "more", "most",
        "other", "some", "such", "no", "not", "only", "same", "so", "than", "too", "very",
        "just", "also", "now", "here", "there", "then", "once", "i", "you", "we", "me", "us",
        "about", "show", "latest", "new", "news", "recent", "any", "current", "tell", "give"
    };

    /// <summary>
    /// RRF constant (standard value from literature). Higher = more uniform blending.
    /// </summary>
    private const int RrfK = 60;

    /// <summary>
    /// Freshness half-life in hours. Content older than this decays exponentially.
    /// </summary>
    private const double FreshnessHalfLifeHours = 48.0;

    // Signal weights for RRF fusion
    private readonly double _bm25Weight;
    private readonly double _freshnessWeight;
    private readonly double _authorityWeight;
    private readonly double _querySimWeight;
    private readonly double _vibeWeight;

    public RelevanceScorer(
        double bm25Weight = 1.0,
        double freshnessWeight = 0.5,
        double authorityWeight = 0.3,
        double querySimWeight = 0.8,
        double vibeWeight = 0.4)
    {
        _bm25Weight = bm25Weight;
        _freshnessWeight = freshnessWeight;
        _authorityWeight = authorityWeight;
        _querySimWeight = querySimWeight;
        _vibeWeight = vibeWeight;
    }

    /// <summary>
    /// Phase 1: Fast scoring with optional embedding boost.
    /// When embeddings and query embedding are provided, query similarity is included
    /// in the fast scoring pass — this provides semantic matching (e.g. "pharmaceutical" matches
    /// "drug pricing") without needing synonym dictionaries.
    /// </summary>
    /// <param name="items">Items to score.</param>
    /// <param name="query">User's search query.</param>
    /// <param name="discardRatio">Fraction of items to discard (0.0-1.0). 0 = keep all.</param>
    /// <param name="queryEmbedding">Pre-computed query embedding for semantic matching (null = text-only).</param>
    /// <returns>Scored items in descending relevance order, with bottom tier discarded.</returns>
    public List<ContentItem> ScoreFast(List<ContentItem> items, string query, double discardRatio = 0.25, float[]? queryEmbedding = null)
    {
        if (items.Count == 0) return items;

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0 && queryEmbedding == null)
        {
            // No meaningful query tokens or embedding — score by freshness + authority only
            foreach (var item in items)
                item.RelevanceScore = ComputeFreshness(item) * 0.7 + NormalizeAuthority(item, items) * 0.3;
            return items.OrderByDescending(i => i.RelevanceScore).ToList();
        }

        // Build BM25 corpus stats from this batch
        var (idf, avgDocLen) = BuildCorpusStats(items);

        // Score each signal independently, then rank
        // Use BM25F (field-weighted) to boost title matches over content matches
        var bm25Scores = items.Select(i => (item: i, score: BM25FScore(i, queryTokens, idf, avgDocLen))).ToList();
        var freshnessScores = items.Select(i => (item: i, score: ComputeFreshness(i))).ToList();
        var authorityScores = items.Select(i => (item: i, score: NormalizeAuthority(i, items))).ToList();

        var signals = new List<(List<(ContentItem item, double score)> scores, double weight)>
        {
            (bm25Scores, _bm25Weight),
            (freshnessScores, _freshnessWeight),
            (authorityScores, _authorityWeight)
        };

        // When embeddings are available, add semantic query similarity in Phase 1
        // This is the key fix: "pharmaceutical" embedding matches "drug pricing" content
        // without needing synonym dictionaries
        List<(ContentItem item, double score)>? querySimScores = null;
        if (queryEmbedding != null)
        {
            querySimScores = items.Select(i => (item: i, score: i.Embedding != null
                ? (double)EmbeddingService.CosineSimilarity(i.Embedding, queryEmbedding)
                : 0.0)).ToList();
            signals.Add((querySimScores, _querySimWeight));
        }

        // RRF fusion across Phase 1 signals
        var rrfScores = FuseRRF(items, signals.ToArray());

        // Apply scores and sort
        foreach (var (item, score) in rrfScores)
            item.RelevanceScore = score;

        var sorted = rrfScores.OrderByDescending(x => x.score).Select(x => x.item).ToList();

        // Hard gate: remove items with near-zero relevance signals.
        // An item survives if it has EITHER decent embedding similarity OR keyword matches.
        // This prevents the gate from being overly aggressive on specific QA queries where
        // search results have good keyword overlap but low embedding similarity.
        if (querySimScores != null)
        {
            var simLookup = querySimScores.ToDictionary(x => x.item.Id, x => x.score);
            var bm25Lookup = bm25Scores.ToDictionary(x => x.item.Id, x => x.score);
            sorted = sorted
                .Where(i => simLookup.GetValueOrDefault(i.Id, 0) >= 0.10 ||
                            bm25Lookup.GetValueOrDefault(i.Id, 0) > 0 ||
                            i.Embedding == null)
                .ToList();
        }

        // Discard bottom tier
        if (discardRatio > 0 && sorted.Count > 3)
        {
            var keepCount = Math.Max(3, (int)(sorted.Count * (1.0 - discardRatio)));
            return sorted.Take(keepCount).ToList();
        }

        return sorted;
    }

    /// <summary>
    /// Phase 2: Full scoring with embedding signals added.
    /// Call after embeddings are computed for remaining items.
    /// </summary>
    /// <param name="items">Items with embeddings set.</param>
    /// <param name="query">User's search query.</param>
    /// <param name="queryEmbedding">Pre-computed query embedding.</param>
    /// <param name="vibeEmbedding">Pre-computed vibe embedding (null = skip vibe signal).</param>
    /// <returns>Items re-ranked with full RRF scores.</returns>
    public List<ContentItem> ScoreFull(
        List<ContentItem> items,
        string query,
        float[] queryEmbedding,
        float[]? vibeEmbedding = null)
    {
        if (items.Count == 0) return items;

        var queryTokens = Tokenize(query);
        var (idf, avgDocLen) = BuildCorpusStats(items);

        // Phase 1 signals (recomputed for refined batch) — BM25F for field weighting
        var bm25Scores = items.Select(i => (item: i, score: BM25FScore(i, queryTokens, idf, avgDocLen))).ToList();
        var freshnessScores = items.Select(i => (item: i, score: ComputeFreshness(i))).ToList();
        var authorityScores = items.Select(i => (item: i, score: NormalizeAuthority(i, items))).ToList();

        // Phase 2 signals (embedding-based)
        var querySim = items.Select(i => (item: i, score: i.Embedding != null
            ? (double)EmbeddingService.CosineSimilarity(i.Embedding, queryEmbedding)
            : 0.0)).ToList();

        var signals = new List<(List<(ContentItem item, double score)> scores, double weight)>
        {
            (bm25Scores, _bm25Weight),
            (freshnessScores, _freshnessWeight),
            (authorityScores, _authorityWeight),
            (querySim, _querySimWeight)
        };

        if (vibeEmbedding != null)
        {
            var vibeSim = items.Select(i => (item: i, score: i.Embedding != null
                ? (double)EmbeddingService.CosineSimilarity(i.Embedding, vibeEmbedding)
                : 0.0)).ToList();
            signals.Add((vibeSim, _vibeWeight));
        }

        var rrfScores = FuseRRF(items, signals.ToArray());

        foreach (var (item, score) in rrfScores)
            item.RelevanceScore = score;

        // Hard gate: remove items with near-zero query embedding similarity.
        // RRF can inflate scores via authority/freshness even when an item has
        // zero topical relevance (e.g., "Grok AI" appearing in "transistor" results).
        // Minimum cosine similarity of 0.10 ensures basic topical alignment.
        var querySimLookup = querySim.ToDictionary(x => x.item.Id, x => x.score);
        var gated = rrfScores
            .Where(x => querySimLookup.GetValueOrDefault(x.item.Id, 0) >= 0.10
                        || x.item.Embedding == null) // keep items without embeddings (can't gate)
            .OrderByDescending(x => x.score)
            .Select(x => x.item)
            .ToList();

        return gated;
    }

    /// <summary>
    /// Reciprocal Rank Fusion across multiple ranking signals.
    /// RRF(d) = Σ weight_i * 1/(k + rank_i(d))
    /// </summary>
    internal static List<(ContentItem item, double score)> FuseRRF(
        List<ContentItem> items,
        (List<(ContentItem item, double score)> scores, double weight)[] signals)
    {
        var fusedScores = new Dictionary<string, double>();
        foreach (var item in items)
            fusedScores[item.Id] = 0;

        foreach (var (scores, weight) in signals)
        {
            if (weight <= 0) continue;

            // Rank by this signal (descending)
            var ranked = scores.OrderByDescending(x => x.score).ToList();
            for (var rank = 0; rank < ranked.Count; rank++)
            {
                var itemId = ranked[rank].item.Id;
                fusedScores[itemId] += weight * (1.0 / (RrfK + rank + 1));
            }
        }

        // Normalize to 0-1 range (guard against all-zero scores)
        var maxScore = fusedScores.Count > 0 ? fusedScores.Values.Max() : 0;
        if (maxScore > 0)
        {
            foreach (var key in fusedScores.Keys.ToList())
                fusedScores[key] /= maxScore;
        }

        return items.Select(i => (i, fusedScores.GetValueOrDefault(i.Id, 0))).ToList();
    }

    #region Signal Computations

    /// <summary>
    /// BM25F scoring: field-weighted BM25 that boosts title matches over content matches.
    /// Title matches are worth TitleBoost× more than content matches.
    /// Falls back to standard BM25 when called with plain text (no field separation).
    /// </summary>
    internal static double BM25Score(
        string docText,
        List<string> queryTokens,
        Dictionary<string, double> idf,
        double avgDocLen)
    {
        const double k1 = 1.5, b = 0.75;

        var docTokens = Tokenize(docText);
        if (docTokens.Count == 0 || avgDocLen < 0.001) return 0;

        var tf = docTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        double score = 0;
        foreach (var term in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!tf.TryGetValue(term, out var freq)) continue;
            var termIdf = idf.GetValueOrDefault(term, 0);
            score += termIdf * freq * (k1 + 1) / (freq + k1 * (1 - b + b * docTokens.Count / avgDocLen));
        }

        return score;
    }

    /// <summary>
    /// BM25F: field-weighted variant that scores title and content separately.
    /// Title matches get a 2.0× boost — a query term in the title is much stronger
    /// evidence of relevance than the same term buried in the body.
    /// </summary>
    internal static double BM25FScore(
        ContentItem item,
        List<string> queryTokens,
        Dictionary<string, double> idf,
        double avgDocLen)
    {
        const double k1 = 1.5, b = 0.75;
        const double titleBoost = 2.0;

        var titleTokens = Tokenize(item.Title);
        var contentTokens = Tokenize(item.Content ?? "");
        var allTokens = titleTokens.Concat(contentTokens).ToList();
        if (allTokens.Count == 0 || avgDocLen < 0.001) return 0;

        // Build field-weighted TF: title occurrences count as titleBoost× content occurrences
        var titleTf = titleTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var contentTf = contentTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        double score = 0;
        foreach (var term in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var titleFreq = titleTf.GetValueOrDefault(term, 0);
            var contentFreq = contentTf.GetValueOrDefault(term, 0);
            var weightedFreq = titleFreq * titleBoost + contentFreq;
            if (weightedFreq < 0.001) continue;

            var termIdf = idf.GetValueOrDefault(term, 0);
            score += termIdf * weightedFreq * (k1 + 1) / (weightedFreq + k1 * (1 - b + b * allTokens.Count / avgDocLen));
        }

        return score;
    }

    /// <summary>
    /// Freshness score: exponential decay from publish/fetch time. Range 0-1.
    /// </summary>
    internal static double ComputeFreshness(ContentItem item)
    {
        var timestamp = item.CreatedAt != default ? item.CreatedAt : item.FetchedAt;
        var ageHours = Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalHours);
        return Math.Exp(-ageHours * Math.Log(2) / FreshnessHalfLifeHours);
    }

    /// <summary>
    /// Normalize source authority (platform score) to 0-1 range relative to batch.
    /// Items without native scores (RSS) get a baseline of 0.3.
    /// </summary>
    internal static double NormalizeAuthority(ContentItem item, List<ContentItem> batch)
    {
        // Sources without native scoring get a decent baseline
        if (item.Score == 0)
        {
            return item.Source switch
            {
                "bbc" or "guardian" or "reuters" or "cnn" => 0.5, // Established news
                "gnews" => 0.4, // Google News (curated)
                _ => 0.3 // Other RSS/web
            };
        }

        // Normalize within batch for items with native scores
        var maxScore = batch.Where(i => i.Source == item.Source && i.Score > 0)
            .Select(i => (double)i.Score)
            .DefaultIfEmpty(1)
            .Max();

        return maxScore > 0 ? Math.Min(1.0, item.Score / maxScore) : 0.3;
    }

    /// <summary>
    /// Build IDF statistics from the current batch (corpus = batch).
    /// </summary>
    internal static (Dictionary<string, double> idf, double avgDocLen) BuildCorpusStats(List<ContentItem> items)
    {
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalLen = 0;
        var corpusSize = items.Count;

        foreach (var item in items)
        {
            var tokens = Tokenize(ItemText(item));
            totalLen += tokens.Count;
            foreach (var t in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
                docFreq[t] = docFreq.GetValueOrDefault(t) + 1;
        }

        var avgDocLen = corpusSize > 0 ? (double)totalLen / corpusSize : 1.0;
        var idf = docFreq.ToDictionary(
            kv => kv.Key,
            kv => Math.Max(0.01, Math.Log((corpusSize - kv.Value + 0.5) / (kv.Value + 0.5) + 1)),
            StringComparer.OrdinalIgnoreCase);

        return (idf, avgDocLen);
    }

    #endregion

    #region Embedding-Based Signals

    /// <summary>
    /// Compute sentiment from embedding similarity to positive/negative anchor texts.
    /// Returns value in [-1, 1] range. Positive = hopeful, negative = concerning.
    /// </summary>
    public static float ComputeEmbeddingSentiment(float[] itemEmbedding, float[] positiveAnchor, float[] negativeAnchor)
    {
        var posSim = EmbeddingService.CosineSimilarity(itemEmbedding, positiveAnchor);
        var negSim = EmbeddingService.CosineSimilarity(itemEmbedding, negativeAnchor);
        return Math.Clamp(posSim - negSim, -1f, 1f);
    }

    /// <summary>
    /// Infer topic from embedding similarity to pre-computed topic anchor embeddings.
    /// Returns best-matching topic name, or "general" if no strong match.
    /// </summary>
    public static string InferTopic(float[] itemEmbedding, Dictionary<string, float[]> topicAnchors, float threshold = 0.25f)
    {
        var bestTopic = "general";
        var bestSim = float.MinValue;
        foreach (var (topic, anchor) in topicAnchors)
        {
            var sim = EmbeddingService.CosineSimilarity(itemEmbedding, anchor);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestTopic = topic;
            }
        }
        return bestSim > threshold ? bestTopic : "general";
    }

    /// <summary>
    /// Topic anchor texts for embedding-based topic inference.
    /// </summary>
    public static readonly Dictionary<string, string> TopicAnchorTexts = new()
    {
        ["technology"] = "technology software programming AI machine learning startup computer digital",
        ["health"] = "health medicine pharmaceutical drug treatment vaccine disease medical hospital",
        ["business"] = "business finance stock market investment economy company earnings revenue",
        ["politics"] = "politics government policy election law regulation legislation vote",
        ["science"] = "science research discovery experiment laboratory physics biology chemistry",
        ["world"] = "international world global conflict diplomacy foreign affairs war peace",
        ["entertainment"] = "entertainment movie music celebrity show film television streaming",
        ["sports"] = "sports game team player championship competition tournament match",
        ["security"] = "cybersecurity vulnerability breach hacking malware threat attack exploit",
        ["climate"] = "climate change environment sustainability emissions carbon renewable energy",
    };

    /// <summary>
    /// Sentiment anchor texts for embedding-based sentiment scoring.
    /// </summary>
    public const string PositiveAnchorText = "positive success innovation breakthrough opportunity progress achievement growth improvement launch exciting";
    public const string NegativeAnchorText = "negative crisis failure risk threat problem decline loss concern warning vulnerability layoff";

    #endregion

    #region MMR (Maximal Marginal Relevance)

    /// <summary>
    /// MMR re-ranking: select items that balance relevance and diversity.
    /// Each selected item maximizes: λ × relevance(item, query) - (1-λ) × max_sim(item, selected).
    /// This prevents the synthesis LLM from receiving 5 articles about the same story.
    /// </summary>
    /// <param name="items">Candidate items with embeddings.</param>
    /// <param name="queryEmbedding">Query embedding for relevance scoring.</param>
    /// <param name="k">Number of items to select.</param>
    /// <param name="lambda">Balance: 1.0 = pure relevance, 0.0 = pure diversity. Default 0.7.</param>
    /// <returns>MMR-reranked selection (most relevant + diverse items first).</returns>
    public static List<ContentItem> MMRSelect(
        List<ContentItem> items,
        float[] queryEmbedding,
        int k = 10,
        double lambda = 0.7)
    {
        if (items.Count <= k) return items;

        var candidates = items.Where(i => i.Embedding != null).ToList();
        var noEmbedding = items.Where(i => i.Embedding == null).ToList();

        if (candidates.Count == 0) return items.Take(k).ToList();

        // Pre-compute query similarities
        var querySim = candidates.ToDictionary(
            c => c.Id,
            c => (double)EmbeddingService.CosineSimilarity(c.Embedding!, queryEmbedding));

        var selected = new List<ContentItem>();
        var remaining = new HashSet<string>(candidates.Select(c => c.Id));

        for (var i = 0; i < Math.Min(k, candidates.Count); i++)
        {
            string? bestId = null;
            var bestMMR = double.MinValue;

            foreach (var id in remaining)
            {
                var candidate = candidates.First(c => c.Id == id);
                var relevance = querySim[id];

                // Max similarity to any already-selected item
                double maxSimToSelected = 0;
                foreach (var s in selected)
                {
                    var sim = (double)EmbeddingService.CosineSimilarity(candidate.Embedding!, s.Embedding!);
                    if (sim > maxSimToSelected) maxSimToSelected = sim;
                }

                var mmrScore = lambda * relevance - (1 - lambda) * maxSimToSelected;
                if (mmrScore > bestMMR)
                {
                    bestMMR = mmrScore;
                    bestId = id;
                }
            }

            if (bestId == null) break;
            selected.Add(candidates.First(c => c.Id == bestId));
            remaining.Remove(bestId);
        }

        // Append items without embeddings at the end
        selected.AddRange(noEmbedding.Take(k - selected.Count));
        return selected;
    }

    #endregion

    #region Text Processing

    /// <summary>
    /// Extract searchable text from a ContentItem (title + content).
    /// </summary>
    internal static string ItemText(ContentItem item) =>
        $"{item.Title} {item.Content ?? ""}".Trim();

    /// <summary>
    /// Tokenize text into lowercase words, filtering stop words.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        return TokenRx.Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .ToList();
    }


    #endregion
}

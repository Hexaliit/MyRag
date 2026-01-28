using System.Text.RegularExpressions;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Two-phase relevance scorer using Reciprocal Rank Fusion (RRF) across multiple signals.
///
/// SCORING PIPELINE OVERVIEW
/// ========================
///
/// Phase 1 — ScoreFast (early discard):
///   Signals: BM25F + Freshness + Authority [+ QuerySimilarity + Quality if embeddings available]
///   Purpose: Discard obviously irrelevant items before expensive embedding computation.
///   Each signal produces an independent ranking; RRF fuses them.
///
/// Phase 2 — ScoreFull (precise ranking):
///   Signals: BM25F + Freshness + Authority + QuerySimilarity + VibeSimilarity + Quality
///   Purpose: Final ranking with all signals including embedding-based semantic matching.
///
/// HOW RRF WORKS
/// =============
/// Each signal ranks all items independently (best=rank 0, worst=rank N).
/// The fusion score for item d is: Σ weight_i × 1/(k + rank_i(d) + 1)
/// where k=60 (standard from literature, prevents top-ranked items from dominating).
///
/// Weight controls how much INFLUENCE a signal's ranking has in the fused score.
/// A weight of 1.0 means the signal's rank contribution is at full strength.
/// A weight of 0.5 means the signal's rank contribution is halved.
/// Weight 0 disables the signal entirely.
///
/// THE SIX SIGNALS
/// ================
///
/// 1. BM25F (text relevance) — weight default: 1.0
///    Field-weighted BM25 scoring. Title 2×, Keywords 2.5×, Content 1×.
///    Uses global IDF corpus when available, falls back to batch-level IDF.
///    Measures: How well do the query's keywords match the item's text?
///
/// 2. Freshness (recency) — weight default: 0.5
///    Exponential decay from the item's publication time (CreatedAt if available,
///    otherwise FetchedAt). Half-life: 48 hours.
///    Formula: exp(-age_hours × ln(2) / 48)
///    At 0h → 1.0, at 48h → 0.5, at 96h → 0.25, at 7d → 0.06
///    Measures: How recent is this item? Newer = higher score.
///
/// 3. Authority (source quality) — weight default: 0.3
///    For items with native scores (HN points, Reddit upvotes): normalized within
///    same-source batch to 0-1 range.
///    For items without scores: hard-coded baseline by source reputation:
///    BBC/Guardian/Reuters=0.5, Google News=0.4, other=0.3.
///    Measures: How trustworthy/popular is this item within its source?
///
/// 4. QuerySimilarity (semantic relevance) — weight default: 0.8
///    Cosine similarity between item embedding and query embedding.
///    Uses all-MiniLM-L6-v2 (384-dim ONNX). Range: -1 to 1, typically 0.1-0.7.
///    Bridges vocabulary gap: "pharmaceutical" matches "drug pricing" without synonyms.
///    Only available in Phase 2, or Phase 1 when embeddings are pre-computed.
///    Measures: Is this item semantically about the query topic?
///
/// 5. VibeSimilarity (tone alignment) — weight default: 0.4
///    Cosine similarity between item embedding and vibe prompt embedding.
///    Promotes items matching the requested tone (doom, hopeful, snarky, etc.)
///    Only available in Phase 2.
///    Measures: Does this item match the desired editorial tone?
///
/// 6. Quality (content substance) — weight default: 0.2
///    Cosine-similarity difference between item embedding and quality anchor embeddings.
///    High-quality anchor = "detailed analysis, well-researched, expert opinion..."
///    Low-quality anchor = "clickbait, shocking, sensational, you won't believe..."
///    Formula: (sim(item, high) - sim(item, low) + 1) / 2 → [0, 1]
///    Only active when WithQualityAnchors() has been called with pre-computed anchors.
///    Measures: Is this substantive journalism or clickbait?
///
/// POST-RRF GATES
/// ==============
/// After RRF fusion, a hard gate removes items with cosine similarity &lt; 0.20.
/// This prevents authority/freshness from inflating scores for topically irrelevant items.
/// Items without embeddings are exempt (can't be gated).
///
/// QUERY-TYPE ADAPTATION
/// =====================
/// ForQueryType() returns a scorer with weights tuned per query type:
///   Roundup:    freshness↑(0.8) bm25↓(0.7) authority↓(0.2) querySim↓(0.5) quality↓(0.15) — recency first
///   Timeline:   freshness↑↑(1.0) bm25↓(0.5) authority↓(0.2) querySim(0.6) quality↓(0.1) — time is paramount
///   Explainer:  freshness↓(0.3) bm25(1.0) authority↑(0.5) querySim↑(1.0) quality↑(0.4) — quality &amp; precision
///   Comparison: freshness↓(0.3) bm25(0.8) authority(0.4) querySim↑(1.0) quality(0.3) — precise matching
///   General:    default weights (quality: 0.2) — balanced for mixed-intent queries
/// </summary>
public partial class RelevanceScorer
{

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
    /// RRF constant k=60 (Cormack et al., 2009). Higher values produce more uniform blending
    /// of ranks; lower values amplify differences between top-ranked and lower-ranked items.
    /// k=60 is the standard value used across most RRF literature and search systems.
    /// </summary>
    private const int RrfK = 60;

    /// <summary>
    /// Freshness half-life in hours. At 48h, an item's freshness score is 0.5.
    /// This decay is applied to CreatedAt (publication time) if available, otherwise FetchedAt.
    /// The freshness score is one input to RRF fusion — it does NOT multiply the final score.
    /// </summary>
    private const double FreshnessHalfLifeHours = 48.0;

    // Signal weights for RRF fusion — see class summary for what each weight controls
    private readonly double _bm25Weight;
    private readonly double _freshnessWeight;
    private readonly double _authorityWeight;
    private readonly double _querySimWeight;
    private readonly double _vibeWeight;
    private readonly double _qualityWeight;

    // Optional quality anchor embeddings — set via WithQualityAnchors()
    private float[]? _highQualityAnchor;
    private float[]? _lowQualityAnchor;

    public RelevanceScorer(
        double bm25Weight = 1.0,
        double freshnessWeight = 0.5,
        double authorityWeight = 0.3,
        double querySimWeight = 0.8,
        double vibeWeight = 0.4,
        double qualityWeight = 0.2)
    {
        _bm25Weight = bm25Weight;
        _freshnessWeight = freshnessWeight;
        _authorityWeight = authorityWeight;
        _querySimWeight = querySimWeight;
        _vibeWeight = vibeWeight;
        _qualityWeight = qualityWeight;
    }

    /// <summary>
    /// Set quality anchor embeddings for content quality scoring.
    /// Quality score = cosine_sim(item, highQuality) - cosine_sim(item, lowQuality),
    /// normalized to [0, 1]. This penalizes clickbait/low-quality content in RRF.
    /// </summary>
    public RelevanceScorer WithQualityAnchors(float[] highQualityAnchor, float[] lowQualityAnchor)
    {
        _highQualityAnchor = highQualityAnchor;
        _lowQualityAnchor = lowQualityAnchor;
        return this;
    }

    /// <summary>
    /// Create a scorer with weights tuned for the detected query type.
    /// Different query types benefit from different signal emphasis:
    /// - Roundup/Timeline: freshness matters most (news recency)
    /// - Explainer: authority and query precision matter most (quality sources)
    /// - Comparison: query similarity matters most (precise matching)
    /// </summary>
    public static RelevanceScorer ForQueryType(QueryType queryType) => queryType switch
    {
        QueryType.Roundup => new RelevanceScorer(
            bm25Weight: 0.7, freshnessWeight: 0.8, authorityWeight: 0.2,
            querySimWeight: 0.5, vibeWeight: 0.4, qualityWeight: 0.15),

        QueryType.Timeline => new RelevanceScorer(
            bm25Weight: 0.5, freshnessWeight: 1.0, authorityWeight: 0.2,
            querySimWeight: 0.6, vibeWeight: 0.3, qualityWeight: 0.1),

        QueryType.Explainer => new RelevanceScorer(
            bm25Weight: 1.0, freshnessWeight: 0.3, authorityWeight: 0.5,
            querySimWeight: 1.0, vibeWeight: 0.3, qualityWeight: 0.4),

        QueryType.Comparison => new RelevanceScorer(
            bm25Weight: 0.8, freshnessWeight: 0.3, authorityWeight: 0.4,
            querySimWeight: 1.0, vibeWeight: 0.3, qualityWeight: 0.3),

        _ => new RelevanceScorer() // General: default weights (quality: 0.2)
    };

    /// <summary>
    /// Create a scorer tuned for knowledge base queries where Authority and Freshness
    /// provide zero discrimination (all items have Score=0 and similar crawl dates).
    /// Zeroes out noise signals and boosts BM25F + QuerySimilarity for precision.
    /// </summary>
    public static RelevanceScorer ForKnowledgeBase(QueryType queryType) => queryType switch
    {
        QueryType.Roundup => new RelevanceScorer(
            bm25Weight: 1.0, freshnessWeight: 0.2, authorityWeight: 0.0,
            querySimWeight: 0.8, vibeWeight: 0.2, qualityWeight: 0.15),

        _ => new RelevanceScorer(
            bm25Weight: 1.5, freshnessWeight: 0.0, authorityWeight: 0.0,
            querySimWeight: 1.2, vibeWeight: 0.2, qualityWeight: 0.3)
    };

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
    /// <param name="globalCorpus">Optional global keyword corpus for proper IDF (keyword → document_count).</param>
    /// <param name="globalCorpusSize">Total document count for global IDF computation.</param>
    /// <returns>Scored items in descending relevance order, with bottom tier discarded.</returns>
    public List<ContentItem> ScoreFast(List<ContentItem> items, string query, double discardRatio = 0.25,
        float[]? queryEmbedding = null,
        Dictionary<string, int>? globalCorpus = null, int? globalCorpusSize = null)
    {
        if (items.Count == 0) return items;

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0 && queryEmbedding == null)
        {
            // No meaningful query tokens or embedding — score by freshness + authority only
            var authScores = ComputeAuthorityScores(items).ToDictionary(x => x.item.Id, x => x.score);
            foreach (var item in items)
                item.RelevanceScore = ComputeFreshness(item) * 0.7 + authScores.GetValueOrDefault(item.Id, 0.3) * 0.3;
            return items.OrderByDescending(i => i.RelevanceScore).ToList();
        }

        // Pre-tokenize all items once — avoids redundant Tokenize() calls in BM25 and corpus stats
        var tokenCache = PreTokenizeItems(items);

        // Build BM25 corpus stats — use global corpus if available for proper IDF
        var (idf, avgDocLen) = BuildCorpusStats(items, globalCorpus, globalCorpusSize, tokenCache);

        // Score each signal independently, then rank
        // Use BM25F (field-weighted) to boost title + keywords matches over content matches
        var bm25Scores = items.Select(i => (item: i, score: BM25FScore(i, queryTokens, idf, avgDocLen, tokenCache))).ToList();
        var freshnessScores = items.Select(i => (item: i, score: ComputeFreshness(i))).ToList();
        var authorityScores = ComputeAuthorityScores(items);

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

        // Content quality signal: penalizes clickbait/low-quality content
        if (_highQualityAnchor != null && _lowQualityAnchor != null)
        {
            var qualityScores = items.Select(i => (item: i, score: i.Embedding != null
                ? ComputeQualityScore(i.Embedding, _highQualityAnchor, _lowQualityAnchor)
                : 0.5)).ToList(); // default to neutral for items without embeddings
            signals.Add((qualityScores, _qualityWeight));
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
                .Where(i => simLookup.GetValueOrDefault(i.Id, 0) >= 0.20 ||
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
    /// <param name="globalCorpus">Optional global keyword corpus for proper IDF.</param>
    /// <param name="globalCorpusSize">Total document count for global IDF computation.</param>
    /// <returns>Items re-ranked with full RRF scores.</returns>
    public List<ContentItem> ScoreFull(
        List<ContentItem> items,
        string query,
        float[] queryEmbedding,
        float[]? vibeEmbedding = null,
        Dictionary<string, int>? globalCorpus = null,
        int? globalCorpusSize = null)
    {
        if (items.Count == 0) return items;

        var queryTokens = Tokenize(query);

        // Pre-tokenize all items once — avoids redundant Tokenize() calls in BM25 and corpus stats
        var tokenCache = PreTokenizeItems(items);
        var (idf, avgDocLen) = BuildCorpusStats(items, globalCorpus, globalCorpusSize, tokenCache);

        // Phase 1 signals (recomputed for refined batch) — BM25F for field weighting
        var bm25Scores = items.Select(i => (item: i, score: BM25FScore(i, queryTokens, idf, avgDocLen, tokenCache))).ToList();
        var freshnessScores = items.Select(i => (item: i, score: ComputeFreshness(i))).ToList();
        var authorityScores = ComputeAuthorityScores(items);

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

        // Content quality signal: penalizes clickbait/low-quality content
        if (_highQualityAnchor != null && _lowQualityAnchor != null)
        {
            var qualityScores = items.Select(i => (item: i, score: i.Embedding != null
                ? ComputeQualityScore(i.Embedding, _highQualityAnchor, _lowQualityAnchor)
                : 0.5)).ToList();
            signals.Add((qualityScores, _qualityWeight));
        }

        var rrfScores = FuseRRF(items, signals.ToArray());

        foreach (var (item, score) in rrfScores)
            item.RelevanceScore = score;

        // Hard gate: remove items with near-zero query embedding similarity.
        // RRF can inflate scores via authority/freshness even when an item has
        // zero topical relevance (e.g., "Grok AI" appearing in "transistor" results).
        // Minimum cosine similarity of 0.20 ensures basic topical alignment.
        var querySimLookup = querySim.ToDictionary(x => x.item.Id, x => x.score);
        var gated = rrfScores
            .Where(x => querySimLookup.GetValueOrDefault(x.item.Id, 0) >= 0.20
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
    /// BM25F: field-weighted variant that scores title, keywords, and content separately.
    /// Title matches get 2.0× boost, keywords get 2.5× boost, content is 1.0× baseline.
    /// Keywords field captures document-level topic profile (structurally weighted terms),
    /// providing a stronger relevance signal than body text alone.
    ///
    /// Enhanced with:
    /// - Fuzzy matching: partial credit for Levenshtein distance ≤ 2
    /// - Phrase proximity: bonus for consecutive query terms in document
    /// </summary>
    internal static double BM25FScore(
        ContentItem item,
        List<string> queryTokens,
        Dictionary<string, double> idf,
        double avgDocLen,
        Dictionary<string, PreTokenized>? tokenCache = null)
    {
        const double k1 = 1.5, b = 0.75;
        const double titleBoost = 2.0;
        const double keywordsBoost = 2.5;
        const double fuzzyDiscount = 0.6;  // Fuzzy matches worth 60% of exact
        const double phraseBonus = 0.3;    // 30% bonus for phrase matches

        List<string> titleTokens, keywordTokens, contentTokens, allTokens;
        if (tokenCache != null && tokenCache.TryGetValue(item.Id, out var pt))
        {
            titleTokens = pt.TitleTokens;
            keywordTokens = pt.KeywordTokens;
            contentTokens = pt.ContentTokens;
            allTokens = pt.AllTokens;
        }
        else
        {
            titleTokens = Tokenize(item.Title);
            keywordTokens = Tokenize(item.Keywords ?? "");
            contentTokens = Tokenize(item.Content ?? "");
            allTokens = titleTokens.Concat(keywordTokens).Concat(contentTokens).ToList();
        }

        if (allTokens.Count == 0 || avgDocLen < 0.001) return 0;

        // Build field-weighted TF: title 2×, keywords 2.5×, content 1×
        var titleTf = titleTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var keywordTf = keywordTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var contentTf = contentTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Build set of all document tokens for fuzzy lookup
        var allTokenSet = new HashSet<string>(allTokens, StringComparer.OrdinalIgnoreCase);

        double score = 0;
        foreach (var term in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var titleFreq = titleTf.GetValueOrDefault(term, 0);
            var keywordFreq = keywordTf.GetValueOrDefault(term, 0);
            var contentFreq = contentTf.GetValueOrDefault(term, 0);
            var weightedFreq = titleFreq * titleBoost + keywordFreq * keywordsBoost + contentFreq;

            // Fuzzy matching: if no exact match, look for close matches
            var matchMultiplier = 1.0;
            if (weightedFreq < 0.001 && term.Length >= 4)
            {
                var fuzzyMatch = FindFuzzyMatch(term, allTokenSet, maxDistance: 2);
                if (fuzzyMatch != null)
                {
                    // Found a fuzzy match — use its frequency with discount
                    titleFreq = titleTf.GetValueOrDefault(fuzzyMatch, 0);
                    keywordFreq = keywordTf.GetValueOrDefault(fuzzyMatch, 0);
                    contentFreq = contentTf.GetValueOrDefault(fuzzyMatch, 0);
                    weightedFreq = titleFreq * titleBoost + keywordFreq * keywordsBoost + contentFreq;
                    matchMultiplier = fuzzyDiscount;
                }
            }

            if (weightedFreq < 0.001) continue;

            var termIdf = idf.GetValueOrDefault(term, 0);
            score += matchMultiplier * termIdf * weightedFreq * (k1 + 1) /
                     (weightedFreq + k1 * (1 - b + b * allTokens.Count / avgDocLen));
        }

        // Phrase proximity bonus: check if query tokens appear consecutively
        if (queryTokens.Count >= 2 && score > 0)
        {
            var phraseMatches = CountPhraseMatches(queryTokens, allTokens);
            if (phraseMatches > 0)
                score *= 1 + phraseBonus * Math.Min(phraseMatches, 3);
        }

        return score;
    }

    /// <summary>
    /// Find a fuzzy match for a query term in the document token set.
    /// Returns the best matching token if Levenshtein distance ≤ maxDistance, else null.
    /// </summary>
    private static string? FindFuzzyMatch(string queryTerm, HashSet<string> docTokens, int maxDistance)
    {
        string? bestMatch = null;
        var bestDistance = maxDistance + 1;

        foreach (var docToken in docTokens)
        {
            // Quick length check — Levenshtein distance is at least |len1 - len2|
            if (Math.Abs(queryTerm.Length - docToken.Length) > maxDistance)
                continue;

            var distance = LevenshteinDistance(queryTerm, docToken);
            if (distance <= maxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                bestMatch = docToken;
                if (distance == 1) break; // Good enough, stop early
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Compute Levenshtein edit distance between two strings.
    /// Optimized with early termination when distance exceeds threshold.
    /// </summary>
    private static int LevenshteinDistance(string s1, string s2)
    {
        if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return 0;

        var len1 = s1.Length;
        var len2 = s2.Length;

        if (len1 == 0) return len2;
        if (len2 == 0) return len1;

        // Use single-row optimization
        var row = new int[len2 + 1];
        for (var j = 0; j <= len2; j++) row[j] = j;

        for (var i = 1; i <= len1; i++)
        {
            var prev = row[0];
            row[0] = i;

            for (var j = 1; j <= len2; j++)
            {
                var curr = row[j];
                var cost = char.ToLowerInvariant(s1[i - 1]) == char.ToLowerInvariant(s2[j - 1]) ? 0 : 1;
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + cost);
                prev = curr;
            }
        }

        return row[len2];
    }

    /// <summary>
    /// Count how many times consecutive query tokens appear consecutively in the document.
    /// Returns the number of phrase matches (bigrams that appear in order).
    /// </summary>
    private static int CountPhraseMatches(List<string> queryTokens, List<string> docTokens)
    {
        if (queryTokens.Count < 2 || docTokens.Count < 2) return 0;

        var matches = 0;
        for (var qi = 0; qi < queryTokens.Count - 1; qi++)
        {
            var q1 = queryTokens[qi];
            var q2 = queryTokens[qi + 1];

            // Check if this bigram appears in the document
            for (var di = 0; di < docTokens.Count - 1; di++)
            {
                if (string.Equals(docTokens[di], q1, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(docTokens[di + 1], q2, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                    break; // Found this bigram, move to next query bigram
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Freshness score: exponential decay from publish/fetch time. Range 0-1.
    /// Falls back to heuristic year detection when CreatedAt is missing/unreliable.
    /// </summary>
    internal static double ComputeFreshness(ContentItem item)
    {
        var timestamp = item.CreatedAt != default ? item.CreatedAt : item.FetchedAt;

        // If CreatedAt was just set to "now" (within 1 min of fetch), it's unreliable
        // Try to detect year from title/content
        var fetchedJustNow = item.CreatedAt != default &&
                             Math.Abs((item.CreatedAt - item.FetchedAt).TotalMinutes) < 1;
        if (fetchedJustNow || item.CreatedAt == default)
        {
            var extractedYear = ExtractYearFromText(item.Title, item.Content);
            if (extractedYear != null)
            {
                // Create a date midway through the detected year
                timestamp = new DateTimeOffset(extractedYear.Value, 6, 15, 0, 0, 0, TimeSpan.Zero);
            }
        }

        var ageHours = Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalHours);
        return Math.Exp(-ageHours * Math.Log(2) / FreshnessHalfLifeHours);
    }

    /// <summary>
    /// Extract a year from article title or content using patterns like "in 2020", "2018 review".
    /// Returns null if no clear year found or if the year is current/future.
    /// </summary>
    private static int? ExtractYearFromText(string? title, string? content)
    {
        var currentYear = DateTime.UtcNow.Year;
        var text = $"{title} {content}";
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Pattern: standalone year (2010-2025) not part of larger number
        var yearPattern = YearInTextPattern();
        var matches = yearPattern.Matches(text);

        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out var year))
            {
                // Only consider past years (not current/future)
                if (year >= 2010 && year < currentYear)
                    return year;
            }
        }
        return null;
    }

    [GeneratedRegex(@"\b(20[12][0-9])\b")]
    private static partial Regex YearInTextPattern();

    /// <summary>
    /// Compute authority scores for all items in a batch. Precomputes max scores per source
    /// to avoid O(N²) repeated LINQ queries.
    /// Returns list of (item, authorityScore) pairs.
    /// </summary>
    internal static List<(ContentItem item, double score)> ComputeAuthorityScores(List<ContentItem> items)
    {
        // Precompute max score per source (O(N) instead of O(N²))
        var maxScoreBySource = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Score <= 0) continue;
            if (!maxScoreBySource.TryGetValue(item.Source, out var current) || item.Score > current)
                maxScoreBySource[item.Source] = item.Score;
        }

        return items.Select(item => (item, NormalizeAuthority(item, maxScoreBySource))).ToList();
    }

    /// <summary>
    /// Normalize source authority (platform score) to 0-1 range using precomputed max scores.
    /// Items without native scores (RSS) get a baseline of 0.3.
    /// </summary>
    internal static double NormalizeAuthority(ContentItem item, Dictionary<string, double> maxScoreBySource)
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

        var maxScore = maxScoreBySource.GetValueOrDefault(item.Source, 1.0);
        return maxScore > 0 ? Math.Min(1.0, item.Score / maxScore) : 0.3;
    }

    /// <summary>
    /// Build IDF statistics from the current batch, or from a global keyword corpus.
    /// When a global corpus is provided, IDF values are computed against the full corpus size
    /// rather than just the current batch, making term weights reliable across queries.
    /// Average document length is always computed from the current batch (items being scored).
    /// </summary>
    internal static (Dictionary<string, double> idf, double avgDocLen) BuildCorpusStats(
        List<ContentItem> items,
        Dictionary<string, int>? globalCorpus = null,
        int? globalCorpusSize = null,
        Dictionary<string, PreTokenized>? tokenCache = null)
    {
        // Always compute avgDocLen from the current batch
        long totalLen = 0;
        var batchDocFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            // Use pre-tokenized data if available (avoids re-tokenizing content)
            var tokens = tokenCache != null && tokenCache.TryGetValue(item.Id, out var pt)
                ? pt.AllTokens
                : Tokenize(ItemText(item));
            totalLen += tokens.Count;
            foreach (var t in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
                batchDocFreq[t] = batchDocFreq.GetValueOrDefault(t) + 1;
        }

        var avgDocLen = items.Count > 0 ? (double)totalLen / items.Count : 1.0;

        // Use global corpus for IDF if available, otherwise fall back to batch
        if (globalCorpus != null && globalCorpusSize is > 0)
        {
            var n = globalCorpusSize.Value;
            // Merge: for terms in the batch that aren't in the global corpus, use batch stats
            var allTerms = new HashSet<string>(batchDocFreq.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in globalCorpus.Keys)
                allTerms.Add(key);

            var idf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in allTerms)
            {
                var df = globalCorpus.GetValueOrDefault(term, batchDocFreq.GetValueOrDefault(term, 1));
                idf[term] = Math.Max(0.01, Math.Log((n - df + 0.5) / (df + 0.5) + 1));
            }

            return (idf, avgDocLen);
        }

        // Fallback: batch-level IDF
        var corpusSize = items.Count;
        var batchIdf = batchDocFreq.ToDictionary(
            kv => kv.Key,
            kv => Math.Max(0.01, Math.Log((corpusSize - kv.Value + 0.5) / (kv.Value + 0.5) + 1)),
            StringComparer.OrdinalIgnoreCase);

        return (batchIdf, avgDocLen);
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

    /// <summary>
    /// Quality anchor texts for embedding-based content quality scoring.
    /// High-quality anchor represents well-researched, substantive journalism.
    /// Low-quality anchor represents clickbait, sensationalism, and thin content.
    /// </summary>
    public const string HighQualityAnchorText = "detailed analysis well-researched investigation expert opinion data-driven report comprehensive review in-depth reporting original research verified sources thorough examination";
    public const string LowQualityAnchorText = "you won't believe shocking revelation this one trick click here sensational breaking exclusive top 10 list clickbait outrage viral celebrity gossip rumor unverified";

    /// <summary>
    /// Compute content quality score from embedding similarity to quality anchors.
    /// Returns value in [0, 1] range. Higher = more substantive/well-researched content.
    /// Uses the same cosine-similarity-difference pattern as sentiment scoring.
    /// </summary>
    public static double ComputeQualityScore(float[] itemEmbedding, float[] highQualityAnchor, float[] lowQualityAnchor)
    {
        var highSim = EmbeddingService.CosineSimilarity(itemEmbedding, highQualityAnchor);
        var lowSim = EmbeddingService.CosineSimilarity(itemEmbedding, lowQualityAnchor);
        // Map from [-1, 1] difference to [0, 1] range
        return Math.Clamp((highSim - lowSim + 1.0) / 2.0, 0, 1);
    }

    /// <summary>
    /// Compute a pseudo-relevance feedback (PRF) centroid from the top-K items.
    /// Averages the embeddings of the best-scored items to create a refined query vector
    /// that captures the "semantic neighborhood" of relevant results.
    /// Blend with the original query embedding: refined = α × original + (1-α) × centroid
    /// </summary>
    /// <param name="items">Items sorted by relevance (best first), with embeddings set.</param>
    /// <param name="originalQueryEmbedding">The original query embedding to blend with.</param>
    /// <param name="topK">Number of top items to average (default 5).</param>
    /// <param name="alpha">Blend factor: 1.0 = pure original, 0.0 = pure centroid. Default 0.7.</param>
    /// <returns>Refined query embedding, or the original if insufficient items have embeddings.</returns>
    public static float[] ComputePRFCentroid(
        List<ContentItem> items,
        float[] originalQueryEmbedding,
        int topK = 5,
        float alpha = 0.7f)
    {
        var topEmbeddings = items
            .Where(i => i.Embedding != null)
            .Take(topK)
            .Select(i => i.Embedding!)
            .ToList();

        if (topEmbeddings.Count < 2)
            return originalQueryEmbedding; // not enough items for meaningful centroid

        var dim = originalQueryEmbedding.Length;
        var centroid = new float[dim];

        // Average the top-K embeddings (SIMD-accelerated accumulation)
        var scale = 1.0f / topEmbeddings.Count;
        foreach (var emb in topEmbeddings)
            VectorMath.AddScaled(centroid, emb, scale);

        // Blend: refined = α × original + (1-α) × centroid (SIMD-accelerated)
        var refined = new float[dim];
        VectorMath.AddScaled(refined, originalQueryEmbedding, alpha);
        VectorMath.AddScaled(refined, centroid, 1 - alpha);

        // L2 normalize the result (SIMD-accelerated)
        VectorMath.L2Normalize(refined);

        return refined;
    }

    #endregion

    #region Outlier Detection

    /// <summary>
    /// Detect off-topic outliers using query-term coverage.
    /// For each item, check what fraction of the distinctive query tokens
    /// (high IDF — rare, topic-defining words) appear in its title or keywords.
    /// Items missing distinctive terms are likely off-topic even if embedding
    /// similarity is moderate (common in same-domain KB collections).
    ///
    /// Returns penalty multipliers: 1.0 = no penalty, &lt;1.0 = penalized.
    /// Should be skipped for Roundup queries (diverse topics expected).
    /// </summary>
    public static Dictionary<string, double> ComputeQueryTermCoverage(
        List<ContentItem> items,
        List<string> queryTokens,
        Dictionary<string, double> idf,
        double idfThreshold = 1.0)
    {
        // Find distinctive query tokens (above-threshold IDF = rare/topic-defining)
        var distinctiveTokens = queryTokens
            .Where(t => idf.GetValueOrDefault(t, 0) >= idfThreshold)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // If no distinctive tokens found, lower threshold and try again
        if (distinctiveTokens.Count == 0)
        {
            var avgIdf = queryTokens.Count > 0
                ? queryTokens.Average(t => idf.GetValueOrDefault(t, 0))
                : 0;
            distinctiveTokens = queryTokens
                .Where(t => idf.GetValueOrDefault(t, 0) >= avgIdf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (distinctiveTokens.Count == 0)
            return new Dictionary<string, double>();

        var penalties = new Dictionary<string, double>();

        foreach (var item in items)
        {
            // Check title + keywords for distinctive query terms
            var itemText = $"{item.Title} {item.Keywords ?? ""}".ToLowerInvariant();
            var covered = distinctiveTokens.Count(t =>
                itemText.Contains(t, StringComparison.OrdinalIgnoreCase));
            var coverage = (double)covered / distinctiveTokens.Count;

            if (coverage >= 0.5)
                continue; // Item covers enough distinctive terms — no penalty

            // Penalty scales with how many distinctive terms are missing
            // coverage 0.0 → penalty 0.3, coverage 0.25 → penalty 0.55, coverage 0.5 → no penalty
            penalties[item.Id] = Math.Max(0.3, coverage * 1.0 + 0.3);
        }

        return penalties;
    }

    /// <summary>
    /// Apply query-term coverage penalties to item relevance scores.
    /// Returns the number of items penalized.
    /// </summary>
    public static int ApplyOutlierPenalties(
        List<ContentItem> items,
        Dictionary<string, double> penalties)
    {
        if (penalties.Count == 0) return 0;

        var penalized = 0;
        foreach (var item in items)
        {
            if (penalties.TryGetValue(item.Id, out var penalty))
            {
                item.RelevanceScore *= penalty;
                penalized++;
            }
        }

        return penalized;
    }

    #endregion

    #region Text Processing

    /// <summary>
    /// Extract searchable text from a ContentItem (title + keywords + content).
    /// </summary>
    internal static string ItemText(ContentItem item) =>
        $"{item.Title} {item.Keywords ?? ""} {item.Content ?? ""}".Trim();

    /// <summary>
    /// Tokenize text into lowercase words, filtering stop words.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        return TokenPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .ToList();
    }

    /// <summary>
    /// Pre-tokenized fields for a ContentItem, avoiding redundant tokenization
    /// across BuildCorpusStats and BM25FScore calls.
    /// </summary>
    internal record PreTokenized(
        List<string> TitleTokens,
        List<string> KeywordTokens,
        List<string> ContentTokens,
        List<string> AllTokens);

    /// <summary>
    /// Pre-tokenize all items once. Returns a lookup by item ID.
    /// Used to avoid re-tokenizing title/keywords/content across BuildCorpusStats and BM25FScore.
    /// </summary>
    internal static Dictionary<string, PreTokenized> PreTokenizeItems(List<ContentItem> items)
    {
        var result = new Dictionary<string, PreTokenized>(items.Count);
        foreach (var item in items)
        {
            var titleTokens = Tokenize(item.Title);
            var keywordTokens = Tokenize(item.Keywords ?? "");
            var contentTokens = Tokenize(item.Content ?? "");
            var allTokens = new List<string>(titleTokens.Count + keywordTokens.Count + contentTokens.Count);
            allTokens.AddRange(titleTokens);
            allTokens.AddRange(keywordTokens);
            allTokens.AddRange(contentTokens);
            result[item.Id] = new PreTokenized(titleTokens, keywordTokens, contentTokens, allTokens);
        }
        return result;
    }

    #endregion

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex TokenPattern();
}

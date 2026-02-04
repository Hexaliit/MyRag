using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Scoring;
using Mostlylucid.DocSummarizer.Services.Utilities;

namespace DoomSummarizer.Services;

/// <summary>
///     Two-phase relevance scorer using Reciprocal Rank Fusion (RRF) across multiple signals.
///     SCORING PIPELINE OVERVIEW
///     ========================
///     Phase 1 — ScoreFast (early discard):
///     Signals: Freshness + Authority [+ QuerySimilarity + Quality if embeddings available]
///     Purpose: Discard obviously irrelevant items before expensive embedding computation.
///     Each signal produces an independent ranking; RRF fuses them.
///     Phase 2 — ScoreFull (precise ranking):
///     Signals: Freshness + Authority + QuerySimilarity + VibeSimilarity + Quality
///     Purpose: Final ranking with all signals including embedding-based semantic matching.
///     NOTE: BM25 keyword matching is handled exclusively by Lucene at the retrieval layer.
///     The scorer does NOT run BM25 — QuerySimilarity (semantic embedding) is the primary
///     relevance signal. BM25 static methods are retained as internal utilities.
///     HOW RRF WORKS
///     =============
///     Each signal ranks all items independently (best=rank 0, worst=rank N).
///     The fusion score for item d is: Σ weight_i × 1/(k + rank_i(d) + 1)
///     where k=60 (standard from literature, prevents top-ranked items from dominating).
///     Weight controls how much INFLUENCE a signal's ranking has in the fused score.
///     A weight of 1.0 means the signal's rank contribution is at full strength.
///     A weight of 0.5 means the signal's rank contribution is halved.
///     Weight 0 disables the signal entirely.
///     THE FIVE SIGNALS
///     ================
///     1. Freshness (recency) — weight default: 0.5
///     Exponential decay from the item's publication time (CreatedAt if available,
///     otherwise FetchedAt). Half-life: 48 hours.
///     Formula: exp(-age_hours × ln(2) / 48)
///     At 0h → 1.0, at 48h → 0.5, at 96h → 0.25, at 7d → 0.06
///     Measures: How recent is this item? Newer = higher score.
///     2. Authority (source quality) — weight default: 0.3
///     For items with native scores (HN points, Reddit upvotes): normalized within
///     same-source batch to 0-1 range.
///     For items without scores: hard-coded baseline by source reputation:
///     BBC/Guardian/Reuters=0.5, Google News=0.4, other=0.3.
///     Measures: How trustworthy/popular is this item within its source?
///     3. QuerySimilarity (semantic relevance) — weight default: 1.5
///     Cosine similarity between item embedding and query embedding.
///     Uses all-MiniLM-L6-v2 (384-dim ONNX). Range: -1 to 1, typically 0.1-0.7.
///     Bridges vocabulary gap: "pharmaceutical" matches "drug pricing" without synonyms.
///     Primary relevance signal now that BM25 is handled at retrieval by Lucene.
///     Measures: Is this item semantically about the query topic?
///     4. VibeSimilarity (tone alignment) — weight default: 0.4
///     Cosine similarity between item embedding and vibe prompt embedding.
///     Promotes items matching the requested tone (doom, hopeful, snarky, etc.)
///     Only available in Phase 2.
///     Measures: Does this item match the desired editorial tone?
///     5. Quality (content substance) — weight default: 0.2
///     Cosine-similarity difference between item embedding and quality anchor embeddings.
///     High-quality anchor = "detailed analysis, well-researched, expert opinion..."
///     Low-quality anchor = "clickbait, shocking, sensational, you won't believe..."
///     Formula: (sim(item, high) - sim(item, low) + 1) / 2 → [0, 1]
///     Only active when WithQualityAnchors() has been called with pre-computed anchors.
///     Measures: Is this substantive journalism or clickbait?
///     POST-RRF GATES
///     ==============
///     After RRF fusion, a hard gate removes items with cosine similarity &lt; threshold.
///     Phase 1 gate: embedding similarity &gt;= 0.25 (items without embeddings are exempt).
///     Phase 2 gate: embedding similarity &gt;= 0.20 using the ORIGINAL query embedding
///     (not PRF-refined) to prevent centroid drift from shifting acceptance toward off-topic content.
///     QUERY-TYPE ADAPTATION
///     =====================
///     ForQueryType() returns a scorer with weights tuned per query type:
///     Roundup:    freshness↑(0.8) authority↓(0.2) querySim(1.0) quality↓(0.15) — recency first
///     Timeline:   freshness↑↑(1.0) authority↓(0.2) querySim(1.0) quality↓(0.1) — time is paramount
///     Explainer:  freshness↓(0.3) authority↑(0.5) querySim↑(1.5) quality↑(0.4) — quality &amp; precision
///     Comparison: freshness↓(0.3) authority(0.4) querySim↑(1.5) quality(0.3) — precise matching
///     General:    default weights (querySim: 1.5, quality: 0.2) — balanced for mixed-intent queries
/// </summary>
public partial class RelevanceScorer
{
    // Use the shared stopword list from DocSummarizer.Core (includes dotnet-stop-words package)
    // See StopwordLists.cs for the full list including honorifics, code keywords, etc.

    /// <summary>
    ///     RRF constant k=60 (Cormack et al., 2009). Higher values produce more uniform blending
    ///     of ranks; lower values amplify differences between top-ranked and lower-ranked items.
    ///     k=60 is the standard value used across most RRF literature and search systems.
    /// </summary>
    private const int RrfK = 60;

    /// <summary>
    ///     Weight for pre-computed text relevance scores (e.g., Lucene FTS) in RRF fusion.
    ///     When Lucene FTS scores are passed through, they provide keyword precision that
    ///     complements the embedding-based QuerySimilarity signal.
    /// </summary>
    private const double TextRelevanceWeight = 1.0;

    /// <summary>
    ///     Default freshness half-life in hours. At 48h, an item's freshness score is 0.5.
    ///     Query-type-specific half-lives: Roundup=24h, Timeline=24h, Explainer=168h, General=48h.
    /// </summary>
    private const double DefaultFreshnessHalfLifeHours = 48.0;

    private readonly double _authorityWeight;

    /// <summary>Instance half-life — set via ForQueryType() to vary decay by query type.</summary>
    private readonly double _freshnessHalfLifeHours;

    // Signal weights for RRF fusion — see class summary for what each weight controls
    private readonly double _freshnessWeight;
    private readonly double _qualityWeight;
    private readonly double _querySimWeight;
    private readonly double _vibeWeight;

    // Optional quality anchor embeddings — set via WithQualityAnchors()
    private float[]? _highQualityAnchor;
    private float[]? _lowQualityAnchor;

    public RelevanceScorer(
        double freshnessWeight = 0.5,
        double authorityWeight = 0.3,
        double querySimWeight = 1.5,
        double vibeWeight = 0.4,
        double qualityWeight = 0.2,
        double freshnessHalfLifeHours = DefaultFreshnessHalfLifeHours)
    {
        _freshnessWeight = freshnessWeight;
        _authorityWeight = authorityWeight;
        _querySimWeight = querySimWeight;
        _vibeWeight = vibeWeight;
        _qualityWeight = qualityWeight;
        _freshnessHalfLifeHours = freshnessHalfLifeHours;
    }

    /// <summary>
    ///     Set quality anchor embeddings for content quality scoring.
    ///     Quality score = cosine_sim(item, highQuality) - cosine_sim(item, lowQuality),
    ///     normalized to [0, 1]. This penalizes clickbait/low-quality content in RRF.
    /// </summary>
    public RelevanceScorer WithQualityAnchors(float[] highQualityAnchor, float[] lowQualityAnchor)
    {
        _highQualityAnchor = highQualityAnchor;
        _lowQualityAnchor = lowQualityAnchor;
        return this;
    }

    /// <summary>
    ///     Create a scorer with weights tuned for the detected query type.
    ///     Different query types benefit from different signal emphasis:
    ///     - Roundup/Timeline: freshness matters most (news recency)
    ///     - Explainer: authority and query precision matter most (quality sources)
    ///     - Comparison: query similarity matters most (precise matching)
    /// </summary>
    public static RelevanceScorer ForQueryType(QueryType queryType)
    {
        return queryType switch
        {
            // Roundup: aggressive freshness decay (24h half-life) — stale news is noise
            QueryType.Roundup => new RelevanceScorer(
                0.8, 0.2,
                1.0, 0.4, 0.15,
                24.0),

            // Timeline: aggressive freshness decay (24h) — recency is paramount
            QueryType.Timeline => new RelevanceScorer(
                1.0, 0.2,
                1.0, 0.3, 0.1,
                24.0),

            // Explainer: relaxed freshness (168h / 7 days) — older quality content is fine
            QueryType.Explainer => new RelevanceScorer(
                0.3, 0.5,
                1.5, 0.3, 0.4,
                168.0),

            // Comparison: relaxed freshness (168h) — precision over recency
            QueryType.Comparison => new RelevanceScorer(
                0.3, 0.4,
                1.5, 0.3, 0.3,
                168.0),

            _ => new RelevanceScorer() // General: default weights + 48h half-life
        };
    }

    /// <summary>
    ///     Create a scorer tuned for knowledge base queries where Authority and Freshness
    ///     provide zero discrimination (all items have Score=0 and similar crawl dates).
    ///     Zeroes out noise signals and boosts QuerySimilarity for precision.
    ///     Keyword matching is handled by Lucene at the retrieval layer.
    /// </summary>
    public static RelevanceScorer ForKnowledgeBase(QueryType queryType)
    {
        return queryType switch
        {
            QueryType.Roundup => new RelevanceScorer(
                0.2, 0.0,
                1.0, 0.2, 0.15),

            _ => new RelevanceScorer(
                0.0, 0.0,
                1.5, 0.2, 0.3)
        };
    }

    /// <summary>
    ///     Phase 1: Fast scoring with optional embedding boost.
    ///     When embeddings and query embedding are provided, query similarity is included
    ///     in the fast scoring pass — this provides semantic matching (e.g. "pharmaceutical" matches
    ///     "drug pricing") without needing synonym dictionaries.
    /// </summary>
    /// <param name="items">Items to score.</param>
    /// <param name="query">User's search query.</param>
    /// <param name="discardRatio">Fraction of items to discard (0.0-1.0). 0 = keep all.</param>
    /// <param name="queryEmbedding">Pre-computed query embedding for semantic matching (null = text-only).</param>
    /// <param name="textRelevanceScores">
    ///     Pre-computed text relevance scores (e.g., from Lucene FTS).
    ///     When provided, included as an RRF signal for keyword precision.
    /// </param>
    /// <param name="querySimOut">Output dictionary for query similarity scores (used by PRF centroid filtering).</param>
    /// <param name="gateThreshold">Minimum cosine similarity threshold for the hard gate.</param>
    /// <returns>Scored items in descending relevance order, with bottom tier discarded.</returns>
    public List<ContentItem> ScoreFast(List<ContentItem> items, string query, double discardRatio = 0.25,
        float[]? queryEmbedding = null,
        Dictionary<string, double>? textRelevanceScores = null,
        Dictionary<string, double>? querySimOut = null,
        float gateThreshold = 0.30f)
    {
        if (items.Count == 0) return items;

        if (string.IsNullOrWhiteSpace(query) && queryEmbedding == null)
        {
            // No meaningful query or embedding — score by freshness + authority only
            var authScores = ComputeAuthorityScores(items).ToDictionary(x => x.item.Id, x => x.score);
            foreach (var item in items)
                item.RelevanceScore = ComputeFreshness(item) * 0.7 + authScores.GetValueOrDefault(item.Id, 0.3) * 0.3;
            return items.OrderByDescending(i => i.RelevanceScore).ToList();
        }

        // Score each signal independently, then rank
        var freshnessScores = items.Select(i => (item: i, score: ComputeFreshnessForQueryType(i))).ToList();
        var authorityScores = ComputeAuthorityScores(items);

        var signals = new List<(List<(ContentItem item, double score)> scores, double weight)>
        {
            (freshnessScores, _freshnessWeight),
            (authorityScores, _authorityWeight)
        };

        // Text relevance signal: pre-computed scores from Lucene FTS or similar.
        // Provides keyword precision that complements semantic similarity.
        if (textRelevanceScores is { Count: > 0 })
        {
            var textScores = items.Select(i => (item: i,
                score: textRelevanceScores.GetValueOrDefault(i.Id, 0.0))).ToList();
            signals.Add((textScores, TextRelevanceWeight));
        }

        // When embeddings are available, add semantic query similarity in Phase 1
        // This is the primary relevance signal: "pharmaceutical" embedding matches "drug pricing"
        // without needing synonym dictionaries
        List<(ContentItem item, double score)>? querySimScores = null;
        if (queryEmbedding != null)
        {
            querySimScores = items.Select(i => (item: i, score: i.Embedding != null
                ? VectorMath.CosineSimilarity(i.Embedding, queryEmbedding)
                : 0.0)).ToList();
            signals.Add((querySimScores, _querySimWeight));

            // Export query-sim scores for reuse by PRF centroid filtering
            if (querySimOut != null)
                foreach (var (item, score) in querySimScores)
                    querySimOut[item.Id] = score;
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

        // Hard gate: remove items with low embedding similarity.
        // Keyword matching is handled by Lucene at retrieval — the scorer gates on
        // semantic similarity only. Items without embeddings are exempt (can't be gated).
        if (querySimScores != null)
        {
            var simLookup = querySimScores.ToDictionary(x => x.item.Id, x => x.score);
            sorted = sorted
                .Where(i => simLookup.GetValueOrDefault(i.Id, 0) >= gateThreshold)
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
    ///     Phase 2: Full scoring with embedding signals added.
    ///     Call after embeddings are computed for remaining items.
    /// </summary>
    /// <param name="items">Items with embeddings set.</param>
    /// <param name="query">User's search query.</param>
    /// <param name="queryEmbedding">Pre-computed query embedding (may be PRF-refined for ranking).</param>
    /// <param name="vibeEmbedding">Pre-computed vibe embedding (null = skip vibe signal).</param>
    /// <param name="gateEmbedding">
    ///     Original (non-PRF) query embedding for the hard gate.
    ///     When null, falls back to queryEmbedding. Separate from queryEmbedding to prevent
    ///     PRF centroid drift from shifting the topical relevance gate.
    /// </param>
    /// <param name="textRelevanceScores">
    ///     Pre-computed text relevance scores (e.g., from Lucene FTS).
    ///     When provided, included as an RRF signal for keyword precision. Items not in the
    ///     dictionary receive score 0 (worst rank in RRF).
    /// </param>
    /// <param name="gateThreshold">Minimum cosine similarity threshold for the hard gate.</param>
    /// <returns>Items re-ranked with full RRF scores.</returns>
    public List<ContentItem> ScoreFull(
        List<ContentItem> items,
        string query,
        float[] queryEmbedding,
        float[]? vibeEmbedding = null,
        float[]? gateEmbedding = null,
        Dictionary<string, double>? textRelevanceScores = null,
        float gateThreshold = 0.20f)
    {
        if (items.Count == 0) return items;

        var freshnessScores = items.Select(i => (item: i, score: ComputeFreshnessForQueryType(i))).ToList();
        var authorityScores = ComputeAuthorityScores(items);

        // Embedding-based signals
        var querySim = items.Select(i => (item: i, score: i.Embedding != null
            ? VectorMath.CosineSimilarity(i.Embedding, queryEmbedding)
            : 0.0)).ToList();

        var signals = new List<(List<(ContentItem item, double score)> scores, double weight)>
        {
            (freshnessScores, _freshnessWeight),
            (authorityScores, _authorityWeight),
            (querySim, _querySimWeight)
        };

        // Text relevance signal: pre-computed scores from Lucene FTS or similar.
        // Provides keyword precision that complements semantic similarity.
        if (textRelevanceScores is { Count: > 0 })
        {
            var textScores = items.Select(i => (item: i,
                score: textRelevanceScores.GetValueOrDefault(i.Id, 0.0))).ToList();
            signals.Add((textScores, TextRelevanceWeight));
        }

        if (vibeEmbedding != null)
        {
            var vibeSim = items.Select(i => (item: i, score: i.Embedding != null
                ? VectorMath.CosineSimilarity(i.Embedding, vibeEmbedding)
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
        //
        // IMPORTANT: Use the original (non-PRF) query embedding for the gate, not the
        // PRF-refined embedding used for ranking. PRF centroid drift can shift the gate's
        // acceptance region toward off-topic content when Phase 1 lets through noise.
        var effectiveGateEmbedding = gateEmbedding ?? queryEmbedding;

        // Reuse querySim scores when gate embedding matches query embedding (common case)
        Dictionary<string, double> gateSim;
        if (ReferenceEquals(effectiveGateEmbedding, queryEmbedding))
        {
            gateSim = new Dictionary<string, double>(querySim.Count);
            foreach (var (item, score) in querySim)
                gateSim[item.Id] = score;
        }
        else
        {
            gateSim = new Dictionary<string, double>(items.Count);
            foreach (var item in items)
                gateSim[item.Id] = item.Embedding != null
                    ? VectorMath.CosineSimilarity(item.Embedding, effectiveGateEmbedding)
                    : 0.0;
        }

        var gated = rrfScores
            .Where(x => gateSim.GetValueOrDefault(x.item.Id, 0) >= gateThreshold)
            .OrderByDescending(x => x.score)
            .Select(x => x.item)
            .ToList();

        return gated;
    }

    /// <summary>
    ///     Reciprocal Rank Fusion across multiple ranking signals.
    ///     Delegates to the shared generic RrfFusion implementation.
    ///     RRF(d) = Σ weight_i * 1/(k + rank_i(d))
    /// </summary>
    internal static List<(ContentItem item, double score)> FuseRRF(
        List<ContentItem> items,
        (List<(ContentItem item, double score)> scores, double weight)[] signals)
    {
        var rankedSignals = signals
            .Select(s => new RankedSignal<ContentItem>(
                s.scores.OrderByDescending(x => x.score).Select(x => x.item).ToList(),
                s.weight));

        var fused = RrfFusion.Fuse(rankedSignals, item => item.Id, RrfK, true);
        var scoreMap = fused.ToDictionary(f => f.Item.Id, f => f.Score);
        return items.Select(i => (i, scoreMap.GetValueOrDefault(i.Id, 0.0))).ToList();
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex TokenPattern();

    #region Signal Computations

    /// <summary>
    ///     BM25F scoring: field-weighted BM25 that boosts title matches over content matches.
    ///     Title matches are worth TitleBoost× more than content matches.
    ///     Falls back to standard BM25 when called with plain text (no field separation).
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
    ///     BM25F: field-weighted variant that scores title, keywords, and content separately.
    ///     Title matches get 2.0× boost, keywords get 2.5× boost, content is 1.0× baseline.
    ///     Keywords field captures document-level topic profile (structurally weighted terms),
    ///     providing a stronger relevance signal than body text alone.
    ///     Enhanced with:
    ///     - Fuzzy matching: partial credit for Levenshtein distance ≤ 2
    ///     - Phrase proximity: bonus for consecutive query terms in document
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
        const double fuzzyDiscount = 0.6; // Fuzzy matches worth 60% of exact
        const double phraseBonus = 0.3; // 30% bonus for phrase matches

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
                var fuzzyMatch = FindFuzzyMatch(term, allTokenSet, 2);
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
    ///     Find a fuzzy match for a query term in the document token set.
    ///     Returns the best matching token if Levenshtein distance ≤ maxDistance, else null.
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
    ///     Compute Levenshtein edit distance between two strings.
    ///     Optimized with early termination when distance exceeds threshold.
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
    ///     Count how many times consecutive query tokens appear consecutively in the document.
    ///     Returns the number of phrase matches (bigrams that appear in order).
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
                if (string.Equals(docTokens[di], q1, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(docTokens[di + 1], q2, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                    break; // Found this bigram, move to next query bigram
                }
        }

        return matches;
    }

    /// <summary>
    ///     Freshness score: exponential decay from publish/fetch time. Range 0-1.
    ///     Falls back to heuristic year detection when CreatedAt is missing/unreliable.
    ///     Uses the default 48h half-life; call the overload for query-type-specific decay.
    /// </summary>
    internal static double ComputeFreshness(ContentItem item)
    {
        return ComputeFreshness(item, DefaultFreshnessHalfLifeHours);
    }

    /// <summary>Freshness score with configurable half-life for query-type-specific decay.</summary>
    internal static double ComputeFreshness(ContentItem item, double halfLifeHours)
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
                // Create a date midway through the detected year
                timestamp = new DateTimeOffset(extractedYear.Value, 6, 15, 0, 0, 0, TimeSpan.Zero);
        }

        var ageHours = Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalHours);
        return Math.Exp(-ageHours * Math.Log(2) / halfLifeHours);
    }

    /// <summary>Instance method: uses the scorer's query-type-specific half-life.</summary>
    internal double ComputeFreshnessForQueryType(ContentItem item)
    {
        return ComputeFreshness(item, _freshnessHalfLifeHours);
    }

    /// <summary>
    ///     Extract a year from article title or content using patterns like "in 2020", "2018 review".
    ///     Returns null if no clear year found or if the year is current/future.
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
            if (int.TryParse(m.Groups[1].Value, out var year))
                // Only consider past years (not current/future)
                if (year >= 2010 && year < currentYear)
                    return year;

        return null;
    }

    [GeneratedRegex(@"\b(20[12][0-9])\b")]
    private static partial Regex YearInTextPattern();

    /// <summary>
    ///     Compute authority scores for all items in a batch. Precomputes max scores per source
    ///     to avoid O(N²) repeated LINQ queries.
    ///     Returns list of (item, authorityScore) pairs.
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
    ///     Normalize source authority (platform score) to 0-1 range using precomputed max scores.
    ///     Items without native scores (RSS) get a baseline of 0.3.
    /// </summary>
    internal static double NormalizeAuthority(ContentItem item, Dictionary<string, double> maxScoreBySource)
    {
        // Sources without native scoring: tiered authority baseline
        if (item.Score == 0)
        {
            // Check URL domain for more granular authority
            var domain = "";
            try
            {
                domain = !string.IsNullOrEmpty(item.Url) ? new Uri(item.Url).Host.ToLowerInvariant() : "";
            }
            catch
            {
                /* ignore malformed URLs */
            }

            // Tier 1: Wire services and premium journalism (0.6)
            if (item.Source is "bbc" or "reuters" ||
                domain.Contains("reuters.com") || domain.Contains("apnews.com") ||
                domain.Contains("bbc.co.uk") || domain.Contains("bbc.com") ||
                domain.Contains("nytimes.com") || domain.Contains("washingtonpost.com") ||
                domain.Contains("theguardian.com") || domain.Contains("economist.com") ||
                domain.Contains("ft.com") || domain.Contains("wsj.com") ||
                domain.Contains("npr.org") || domain.Contains("pbs.org"))
                return 0.6;

            // Tier 2: Established news and curated feeds (0.45)
            if (item.Source is "guardian" or "cnn" or "gnews" or "newsapi" or "newsdata" or "currents" ||
                domain.Contains("cnn.com") || domain.Contains("politico.com") ||
                domain.Contains("theatlantic.com") || domain.Contains("arstechnica.com") ||
                domain.Contains("theverge.com") || domain.Contains("techcrunch.com") ||
                domain.Contains("wired.com") || domain.Contains("nature.com") ||
                domain.Contains("science.org") || domain.Contains("theregister.com"))
                return 0.45;

            // Tier 3: Search results and aggregated content (0.30)
            if (item.Source is "brave_search" or "serper" or "tavily" or "google_search" or "duckduckgo" or "jina")
                return 0.30;

            // Tier 4: Community/social (0.25)
            if (item.Source is "hn" or "reddit" or "lobsters" or "devto" ||
                domain.Contains("reddit.com") || domain.Contains("medium.com") ||
                domain.Contains("dev.to") || domain.Contains("hackernoon.com"))
                return 0.25;

            return 0.30; // Default fallback
        }

        var maxScore = maxScoreBySource.GetValueOrDefault(item.Source, 1.0);
        return maxScore > 0 ? Math.Min(1.0, item.Score / maxScore) : 0.3;
    }

    /// <summary>
    ///     Build IDF statistics from the current batch, or from a global keyword corpus.
    ///     When a global corpus is provided, IDF values are computed against the full corpus size
    ///     rather than just the current batch, making term weights reliable across queries.
    ///     Average document length is always computed from the current batch (items being scored).
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
    ///     Compute sentiment from embedding similarity to positive/negative anchor texts.
    ///     Returns value in [-1, 1] range. Positive = hopeful, negative = concerning.
    /// </summary>
    public static float ComputeEmbeddingSentiment(float[] itemEmbedding, float[] positiveAnchor, float[] negativeAnchor)
    {
        var posSim = VectorMath.CosineSimilarity(itemEmbedding, positiveAnchor);
        var negSim = VectorMath.CosineSimilarity(itemEmbedding, negativeAnchor);
        return Math.Clamp(posSim - negSim, -1f, 1f);
    }

    /// <summary>
    ///     Infer topic from embedding similarity to pre-computed topic anchor embeddings.
    ///     Returns best-matching topic name, or "general" if no strong match.
    /// </summary>
    public static string InferTopic(float[] itemEmbedding, Dictionary<string, float[]> topicAnchors,
        float threshold = 0.25f)
    {
        var bestTopic = "general";
        var bestSim = float.MinValue;
        foreach (var (topic, anchor) in topicAnchors)
        {
            var sim = VectorMath.CosineSimilarity(itemEmbedding, anchor);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestTopic = topic;
            }
        }

        return bestSim > threshold ? bestTopic : "general";
    }

    /// <summary>
    ///     Topic anchor texts for embedding-based topic inference.
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
        ["climate"] = "climate change environment sustainability emissions carbon renewable energy"
    };

    /// <summary>
    ///     Sentiment anchor texts for embedding-based sentiment scoring.
    /// </summary>
    public const string PositiveAnchorText =
        "positive success innovation breakthrough opportunity progress achievement growth improvement launch exciting";

    public const string NegativeAnchorText =
        "negative crisis failure risk threat problem decline loss concern warning vulnerability layoff";

    /// <summary>
    ///     Quality anchor texts for embedding-based content quality scoring.
    ///     High-quality anchor represents well-researched, substantive journalism.
    ///     Low-quality anchor represents clickbait, sensationalism, and thin content.
    /// </summary>
    public const string HighQualityAnchorText =
        "detailed analysis well-researched investigation expert opinion data-driven report comprehensive review in-depth reporting original research verified sources thorough examination";

    public const string LowQualityAnchorText =
        "you won't believe shocking revelation this one trick click here sensational breaking exclusive top 10 list clickbait outrage viral celebrity gossip rumor unverified";

    /// <summary>
    ///     Compute content quality score from embedding similarity to quality anchors.
    ///     Returns value in [0, 1] range. Higher = more substantive/well-researched content.
    ///     Uses the same cosine-similarity-difference pattern as sentiment scoring.
    /// </summary>
    public static double ComputeQualityScore(float[] itemEmbedding, float[] highQualityAnchor, float[] lowQualityAnchor)
    {
        var highSim = VectorMath.CosineSimilarity(itemEmbedding, highQualityAnchor);
        var lowSim = VectorMath.CosineSimilarity(itemEmbedding, lowQualityAnchor);
        // Map from [-1, 1] difference to [0, 1] range
        return Math.Clamp((highSim - lowSim + 1.0) / 2.0, 0, 1);
    }

    /// <summary>
    ///     Compute a pseudo-relevance feedback (PRF) centroid from the top-K items.
    ///     Averages the embeddings of the best-scored items to create a refined query vector
    ///     that captures the "semantic neighborhood" of relevant results.
    ///     Blend with the original query embedding: refined = α × original + (1-α) × centroid
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
    ///     Detect off-topic outliers using embedding similarity.
    ///     Items with low semantic similarity to the query are penalized.
    ///     This catches cases where string matching fails (e.g., "Google Auth" for "authentication").
    ///     Returns penalty multipliers: 1.0 = no penalty, &lt;1.0 = penalized.
    ///     Should be skipped for Roundup queries (diverse topics expected).
    /// </summary>
    public static Dictionary<string, double> ComputeQueryTermCoverage(
        List<ContentItem> items,
        List<string> queryTokens,
        Dictionary<string, double> idf,
        double idfThreshold = 1.0,
        float[]? queryEmbedding = null)
    {
        var penalties = new Dictionary<string, double>();

        // If we have a query embedding, use semantic similarity for outlier detection
        if (queryEmbedding != null)
        {
            foreach (var item in items)
            {
                if (item.Embedding == null) continue;

                var similarity = VectorMath.CosineSimilarity(queryEmbedding, item.Embedding);

                // Items with decent semantic similarity (>= 0.25) are not penalized
                if (similarity >= 0.25) continue;

                // Low similarity items get penalized proportionally
                // sim 0.20 → penalty 0.8, sim 0.10 → penalty 0.5, sim 0.0 → penalty 0.3
                var penalty = Math.Max(0.3, similarity * 2.0 + 0.3);
                penalties[item.Id] = penalty;
            }

            return penalties;
        }

        // Fallback: string-based coverage when no query embedding available
        var distinctiveTokens = queryTokens
            .Where(t => idf.GetValueOrDefault(t, 0) >= idfThreshold)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            return penalties;

        foreach (var item in items)
        {
            var contentPreview = item.Content?.Length > 1000
                ? item.Content[..1000]
                : item.Content ?? "";
            var itemText = $"{item.Title} {item.Keywords ?? ""} {contentPreview}".ToLowerInvariant();
            var covered = distinctiveTokens.Count(t =>
                itemText.Contains(t, StringComparison.OrdinalIgnoreCase));
            var coverage = (double)covered / distinctiveTokens.Count;

            if (coverage >= 0.5) continue;

            penalties[item.Id] = Math.Max(0.3, coverage * 1.0 + 0.3);
        }

        return penalties;
    }

    /// <summary>
    ///     Apply query-term coverage penalties to item relevance scores.
    ///     Returns the number of items penalized.
    /// </summary>
    public static int ApplyOutlierPenalties(
        List<ContentItem> items,
        Dictionary<string, double> penalties)
    {
        if (penalties.Count == 0) return 0;

        var penalized = 0;
        foreach (var item in items)
            if (penalties.TryGetValue(item.Id, out var penalty))
            {
                item.RelevanceScore *= penalty;
                penalized++;
            }

        return penalized;
    }

    #endregion

    #region Text Processing

    /// <summary>
    ///     Extract searchable text from a ContentItem (title + keywords + content).
    /// </summary>
    internal static string ItemText(ContentItem item)
    {
        return $"{item.Title} {item.Keywords ?? ""} {item.Content ?? ""}".Trim();
    }

    /// <summary>
    ///     Tokenize text into lowercase words, filtering stop words.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        return TokenPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopwordLists.IsStopword(t))
            .ToList();
    }

    /// <summary>
    ///     Pre-tokenized fields for a ContentItem, avoiding redundant tokenization
    ///     across BuildCorpusStats and BM25FScore calls.
    /// </summary>
    internal record PreTokenized(
        List<string> TitleTokens,
        List<string> KeywordTokens,
        List<string> ContentTokens,
        List<string> AllTokens);

    /// <summary>
    ///     Pre-tokenize all items once. Returns a lookup by item ID.
    ///     Used to avoid re-tokenizing title/keywords/content across BuildCorpusStats and BM25FScore.
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
}
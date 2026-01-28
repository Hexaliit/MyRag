namespace DoomSummarizer.Services;

/// <summary>
/// Extracts document-level keywords using structural weighting and n-grams.
/// No LLM — entirely deterministic using existing tokenization from RelevanceScorer.
///
/// Structural weighting rationale:
///   Title:    4.0× — Author's chosen label, strongest topic signal
///   Headings: 3.0× — Section topics summarize document themes
///   Intro:    2.0× — Opening sets context and thesis
///   Body:     1.0× — Baseline supporting detail
/// </summary>
public static class DocumentProfileService
{
    private const double TitleWeight = 4.0;
    private const double HeadingWeight = 3.0;
    private const double IntroWeight = 2.0;
    private const double BodyWeight = 1.0;
    private const int TopUnigrams = 20;
    private const int TopBigramCount = 10;
    private const int MinBigramOccurrences = 2;

    /// <summary>
    /// Extract top-K keywords with structural weighting.
    /// </summary>
    public static DocumentProfile ExtractProfile(string title, string content)
    {
        // 1. Parse structure: extract headings, intro paragraphs, body
        var headings = ExtractHeadingTexts(content);
        var introParagraphs = ExtractIntroParagraphs(content, count: 2);

        // 2. Tokenize each zone (reuse RelevanceScorer.Tokenize + stop words)
        var headingTokens = RelevanceScorer.Tokenize(string.Join(" ", headings));
        var introTokens = RelevanceScorer.Tokenize(string.Join(" ", introParagraphs));
        var bodyTokens = RelevanceScorer.Tokenize(content);
        var titleTokens = RelevanceScorer.Tokenize(title);

        // 3. Compute weighted term frequencies
        var weightedTf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AccumulateWeighted(weightedTf, titleTokens, TitleWeight);
        AccumulateWeighted(weightedTf, headingTokens, HeadingWeight);
        AccumulateWeighted(weightedTf, introTokens, IntroWeight);
        AccumulateWeighted(weightedTf, bodyTokens, BodyWeight);

        // 4. Extract bigrams from full text (min 2 occurrences)
        var bigrams = ExtractNgrams(bodyTokens, n: 2, minCount: MinBigramOccurrences);

        // 5. Return top unigrams + top bigrams
        var topKeywords = weightedTf
            .OrderByDescending(kv => kv.Value)
            .Take(TopUnigrams)
            .Select(kv => new KeywordEntry(kv.Key, kv.Value))
            .ToList();

        var topBigrams = bigrams.Take(TopBigramCount).ToList();

        return new DocumentProfile
        {
            TopKeywords = topKeywords,
            TopBigrams = topBigrams,
            KeywordsText = BuildKeywordsText(weightedTf, bigrams)
        };
    }

    private static void AccumulateWeighted(
        Dictionary<string, double> weightedTf,
        List<string> tokens,
        double weight)
    {
        foreach (var token in tokens)
        {
            weightedTf[token] = weightedTf.GetValueOrDefault(token) + weight;
        }
    }

    /// <summary>
    /// N-gram extraction: sliding window over tokens, filtered by minimum occurrence count.
    /// </summary>
    private static List<(string ngram, int count)> ExtractNgrams(
        List<string> tokens, int n, int minCount)
    {
        var ngrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i <= tokens.Count - n; i++)
        {
            var ngram = string.Join(" ", tokens.GetRange(i, n));
            ngrams[ngram] = ngrams.GetValueOrDefault(ngram) + 1;
        }

        return ngrams
            .Where(kv => kv.Value >= minCount)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Extract heading text from markdown (lines starting with #).
    /// </summary>
    private static List<string> ExtractHeadingTexts(string content)
    {
        return content.Split('\n')
            .Where(l => l.TrimStart().StartsWith('#'))
            .Select(l => l.TrimStart().TrimStart('#').Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Extract first N non-empty, non-heading paragraphs (intro content).
    /// </summary>
    private static List<string> ExtractIntroParagraphs(string content, int count)
    {
        return content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.TrimStart().StartsWith('#') && p.Trim().Length > 50)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Build concatenated keywords text for FTS5 indexing and BM25F scoring.
    /// </summary>
    private static string BuildKeywordsText(
        Dictionary<string, double> weightedTf,
        List<(string ngram, int count)> bigrams)
    {
        var topUnigrams = weightedTf
            .OrderByDescending(kv => kv.Value)
            .Take(TopUnigrams)
            .Select(kv => kv.Key);
        var topBigrams = bigrams.Take(TopBigramCount).Select(b => b.ngram);
        return string.Join(" ", topUnigrams.Concat(topBigrams));
    }
}

public record DocumentProfile
{
    public List<KeywordEntry> TopKeywords { get; init; } = [];
    public List<(string ngram, int count)> TopBigrams { get; init; } = [];
    public string KeywordsText { get; init; } = "";
}

public record KeywordEntry(string Keyword, double Weight);

using DoomSummarizer.Services;

namespace DoomSummarizer.Core.Services;

/// <summary>
/// Verifies query terms against a Lucene index to detect terms the corpus has no knowledge of.
/// Uses RelevanceScorer.Tokenize (shared stop words from StopwordLists) and strict TermQuery
/// lookups — no fuzzy matching, no stemming, no query parsing.
/// </summary>
public static class TermVerifier
{
    /// <summary>
    /// Tokenize the query using the shared pipeline (RelevanceScorer.Tokenize + StopwordLists),
    /// merge with any known entities, then check each term against the Lucene inverted index
    /// using strict TermQuery. Returns terms with zero hits (DF=0 in the corpus).
    /// </summary>
    /// <param name="query">The user's natural language query</param>
    /// <param name="dataPath">Storage data path (parent of Lucene indexes)</param>
    /// <param name="collectionName">Lucene index collection name</param>
    /// <param name="sourceFilters">Optional source filters to restrict term check to specific corpus(es)</param>
    /// <param name="knownEntities">Optional named entities (from sentinel + NER) to also verify</param>
    /// <returns>List of missing terms, or null if all terms were found</returns>
    public static List<string>? Verify(
        string query,
        string dataPath,
        string collectionName,
        IReadOnlyList<string>? sourceFilters = null,
        params List<string>?[] knownEntities)
    {
        // Use the shared tokenizer — same stop words as BM25, keyword extraction, etc.
        var terms = new HashSet<string>(
            RelevanceScorer.Tokenize(query),
            StringComparer.OrdinalIgnoreCase);

        // Also verify any entity names from sentinel/NER
        foreach (var entityList in knownEntities)
            if (entityList != null)
                foreach (var e in entityList.Where(e => e.Length >= 3))
                    // Tokenize entity names too (multi-word entities become individual terms)
                    foreach (var token in RelevanceScorer.Tokenize(e))
                        terms.Add(token);

        if (terms.Count == 0)
            return null;

        try
        {
            var lucenePath = Path.Combine(dataPath, "lucene", collectionName);
            if (!Directory.Exists(lucenePath))
                return terms.ToList(); // No index at all — every term is missing

            using var lucene = new LuceneSearchService(lucenePath);
            lucene.Open();

            if (lucene.DocumentCount == 0)
                return terms.ToList(); // Empty index — every term is missing

            var missing = new List<string>();
            foreach (var term in terms)
            {
                // Strict TermQuery — no fuzzy, no stemming. Exact inverted-index lookup.
                if (!lucene.ContainsTerm(term, sourceFilters))
                    missing.Add(term);
            }

            return missing.Count > 0 ? missing : null;
        }
        catch
        {
            return null; // Term verification is best-effort
        }
    }
}

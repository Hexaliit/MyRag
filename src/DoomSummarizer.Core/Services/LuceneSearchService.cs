using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace DoomSummarizer.Services;

/// <summary>
/// Lucene.NET-based full-text search service with file-based index persistence.
/// Provides advanced search features: fuzzy matching, phrase proximity, field boosting.
///
/// Index is stored on disk and reused across sessions. Documents are indexed by ID
/// to support incremental updates without full rebuilds.
///
/// Architecture:
/// - FSDirectory: file-based index storage (persisted)
/// - StandardAnalyzer: tokenization + lowercasing + stop words
/// - BM25Similarity: modern relevance scoring (default in Lucene 4.8+)
/// - MultiFieldQueryParser: searches across title, keywords, content with boosts
/// </summary>
public sealed class LuceneSearchService : IDisposable
{
    private const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;

    // Field names and boosts
    private const string FieldId = "id";
    private const string FieldTitle = "title";
    private const string FieldKeywords = "keywords";
    private const string FieldContent = "content";
    private const string FieldSource = "source";
    private const string FieldUrl = "url";

    private static readonly Dictionary<string, float> FieldBoosts = new()
    {
        [FieldTitle] = 3.0f,      // Title matches most important
        [FieldKeywords] = 2.5f,   // Keywords are topic-defining
        [FieldContent] = 1.0f    // Content is baseline
    };

    private readonly FSDirectory _directory;
    private readonly StandardAnalyzer _analyzer;
    private IndexWriter? _writer;
    private DirectoryReader? _reader;
    private IndexSearcher? _searcher;
    private readonly object _lock = new();

    public string IndexPath { get; }
    public bool IsOpen => _writer != null;

    /// <summary>
    /// Create a Lucene search service with file-based index at the specified path.
    /// </summary>
    public LuceneSearchService(string indexPath)
    {
        IndexPath = indexPath;
        System.IO.Directory.CreateDirectory(indexPath);

        _directory = FSDirectory.Open(indexPath);
        _analyzer = new StandardAnalyzer(AppLuceneVersion);
    }

    /// <summary>
    /// Open the index for reading and writing.
    /// Creates a new index if one doesn't exist.
    /// </summary>
    public void Open()
    {
        lock (_lock)
        {
            if (_writer != null) return;

            var config = new IndexWriterConfig(AppLuceneVersion, _analyzer)
            {
                OpenMode = OpenMode.CREATE_OR_APPEND
            };

            _writer = new IndexWriter(_directory, config);
            RefreshReader();
        }
    }

    /// <summary>
    /// Index a content item. If an item with the same ID exists, it's updated.
    /// </summary>
    public void IndexItem(ContentItem item)
    {
        if (_writer == null) throw new InvalidOperationException("Index not open");

        var doc = new Document
        {
            new StringField(FieldId, item.Id, Field.Store.YES),
            new TextField(FieldTitle, item.Title ?? "", Field.Store.YES),
            new TextField(FieldKeywords, item.Keywords ?? "", Field.Store.YES),
            new TextField(FieldContent, item.Content ?? "", Field.Store.NO),
            new StringField(FieldSource, item.Source ?? "", Field.Store.YES),
            new StringField(FieldUrl, item.Url ?? "", Field.Store.YES)
        };

        // Update or insert (delete existing, then add)
        _writer.UpdateDocument(new Term(FieldId, item.Id), doc);
    }

    /// <summary>
    /// Index multiple items in a batch. More efficient than individual calls.
    /// </summary>
    public void IndexItems(IEnumerable<ContentItem> items)
    {
        foreach (var item in items)
            IndexItem(item);
    }

    /// <summary>
    /// Commit pending changes and refresh the reader for searching.
    /// </summary>
    public void Commit()
    {
        lock (_lock)
        {
            _writer?.Commit();
            RefreshReader();
        }
    }

    /// <summary>
    /// Search the index with Lucene query syntax.
    /// Supports: fuzzy (~), phrase ("..."), boosting (^), AND/OR, field-specific (title:...)
    ///
    /// Examples:
    /// - "htmx asp.net" → phrase search
    /// - htmx~ → fuzzy match (finds "htms", "htxm")
    /// - title:htmx^3 → boost title matches
    /// - htmx AND "asp.net core" → boolean + phrase
    /// </summary>
    public List<LuceneSearchResult> Search(string query, string? sourceFilter = null, int limit = 50)
        => Search(query, sourceFilter != null ? [sourceFilter] : null, limit);

    public List<LuceneSearchResult> Search(string query, IReadOnlyList<string>? sourceFilters, int limit)
    {
        if (_searcher == null || string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            // Build multi-field query with boosts
            var parser = new MultiFieldQueryParser(
                AppLuceneVersion,
                [FieldTitle, FieldKeywords, FieldContent],
                _analyzer,
                FieldBoosts)
            {
                DefaultOperator = Operator.OR,
                AllowLeadingWildcard = false,
                FuzzyMinSim = 0.7f,  // Fuzzy threshold
                PhraseSlop = 2       // Phrase proximity tolerance
            };

            var luceneQuery = ApplySourceFilter(parser.Parse(EscapeSpecialChars(query)), sourceFilters);

            var topDocs = _searcher.Search(luceneQuery, limit);

            return topDocs.ScoreDocs.Select(sd =>
            {
                var doc = _searcher.Doc(sd.Doc);
                return new LuceneSearchResult
                {
                    Id = doc.Get(FieldId),
                    Title = doc.Get(FieldTitle),
                    Source = doc.Get(FieldSource),
                    Url = doc.Get(FieldUrl),
                    Score = sd.Score
                };
            }).ToList();
        }
        catch (ParseException)
        {
            // Query syntax error — fall back to simple term query
            return SearchSimple(query, sourceFilters, limit);
        }
    }

    /// <summary>
    /// Simple search without query parsing (for fallback).
    /// </summary>
    private List<LuceneSearchResult> SearchSimple(string query, IReadOnlyList<string>? sourceFilters, int limit)
    {
        if (_searcher == null) return [];

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];

        var contentQuery = new BooleanQuery();
        foreach (var term in terms)
        {
            foreach (var field in new[] { FieldTitle, FieldKeywords, FieldContent })
            {
                var boost = FieldBoosts.GetValueOrDefault(field, 1.0f);
                var fuzzyQuery = new FuzzyQuery(new Term(field, term), 2) { Boost = boost };
                contentQuery.Add(fuzzyQuery, Occur.SHOULD);
            }
        }

        var finalQuery = ApplySourceFilter(contentQuery, sourceFilters);
        var topDocs = _searcher.Search(finalQuery, limit);

        return topDocs.ScoreDocs.Select(sd =>
        {
            var doc = _searcher.Doc(sd.Doc);
            return new LuceneSearchResult
            {
                Id = doc.Get(FieldId),
                Title = doc.Get(FieldTitle),
                Source = doc.Get(FieldSource),
                Url = doc.Get(FieldUrl),
                Score = sd.Score
            };
        }).ToList();
    }

    /// <summary>
    /// Search with automatic fuzzy enhancement for better recall.
    /// Appends ~ to terms that look like they could benefit from fuzzy matching.
    /// </summary>
    public List<LuceneSearchResult> SearchWithFuzzy(string query, string? sourceFilter = null, int limit = 50)
        => SearchWithFuzzy(query, sourceFilter != null ? [sourceFilter] : null, limit);

    public List<LuceneSearchResult> SearchWithFuzzy(string query, IReadOnlyList<string>? sourceFilters, int limit)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var enhanced = string.Join(" ", terms.Select(t =>
            t.Length >= 4 && !t.Contains('~') && !t.Contains('"') && !t.Contains(':')
                ? $"{t}~"
                : t));

        return Search(enhanced, sourceFilters, limit);
    }

    /// <summary>
    /// Suggest document titles matching a prefix query.
    /// Uses PrefixQuery on title and keywords fields for fast autocomplete.
    /// </summary>
    public List<LuceneSearchResult> Suggest(string prefix, string? sourceFilter = null, int limit = 8)
        => Suggest(prefix, sourceFilter != null ? [sourceFilter] : null, limit);

    public List<LuceneSearchResult> Suggest(string prefix, IReadOnlyList<string>? sourceFilters, int limit)
    {
        if (_searcher == null || string.IsNullOrWhiteSpace(prefix))
            return [];

        var normalizedPrefix = prefix.Trim().ToLowerInvariant();
        var terms = normalizedPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];

        var boolQuery = new BooleanQuery();

        // For each input term, add prefix queries on title and keywords
        foreach (var term in terms)
        {
            var termBool = new BooleanQuery();
            termBool.Add(new PrefixQuery(new Term(FieldTitle, term)) { Boost = 3.0f }, Occur.SHOULD);
            termBool.Add(new PrefixQuery(new Term(FieldKeywords, term)) { Boost = 2.0f }, Occur.SHOULD);
            termBool.Add(new PrefixQuery(new Term(FieldContent, term)), Occur.SHOULD);
            boolQuery.Add(termBool, Occur.MUST);
        }

        var finalQuery = ApplySourceFilter(boolQuery, sourceFilters);
        var topDocs = _searcher.Search(finalQuery, limit);

        // Deduplicate by title
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LuceneSearchResult>();

        foreach (var sd in topDocs.ScoreDocs)
        {
            var doc = _searcher.Doc(sd.Doc);
            var title = doc.Get(FieldTitle) ?? "";
            if (!seen.Add(title)) continue;

            results.Add(new LuceneSearchResult
            {
                Id = doc.Get(FieldId),
                Title = title,
                Source = doc.Get(FieldSource),
                Url = doc.Get(FieldUrl),
                Score = sd.Score
            });
        }

        return results;
    }

    /// <summary>
    /// Get document count in the index.
    /// </summary>
    public int DocumentCount => _reader?.NumDocs ?? 0;

    /// <summary>
    /// Check if a document with the given ID exists in the index.
    /// </summary>
    public bool ContainsDocument(string id)
    {
        if (_searcher == null) return false;

        var query = new TermQuery(new Term(FieldId, id));
        var topDocs = _searcher.Search(query, 1);
        return topDocs.TotalHits > 0;
    }

    /// <summary>
    /// Check if an exact term exists in the content/title/keywords fields.
    /// Uses TermQuery — no fuzzy matching, no stemming, no query parsing.
    /// The term must be lowercased (StandardAnalyzer lowercases at index time).
    /// </summary>
    public bool ContainsTerm(string term, string? sourceFilter = null)
        => ContainsTerm(term, sourceFilter != null ? [sourceFilter] : null);

    public bool ContainsTerm(string term, IReadOnlyList<string>? sourceFilters)
    {
        if (_searcher == null || string.IsNullOrWhiteSpace(term)) return false;

        var lowered = term.ToLowerInvariant();

        // Check across all text fields — any hit means the term exists in the corpus
        foreach (var field in new[] { FieldContent, FieldTitle, FieldKeywords })
        {
            var query = ApplySourceFilter(new TermQuery(new Term(field, lowered)), sourceFilters);
            var topDocs = _searcher.Search(query, 1);
            if (topDocs.TotalHits > 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Build a Lucene query clause for filtering by one or more source values (OR semantics).
    /// Returns null if no sources specified.
    /// </summary>
    private static Query? BuildSourceIncludeFilter(IReadOnlyList<string>? sources)
    {
        if (sources is not { Count: > 0 }) return null;
        if (sources.Count == 1) return new TermQuery(new Term(FieldSource, sources[0]));

        var boolQuery = new BooleanQuery();
        foreach (var s in sources)
            boolQuery.Add(new TermQuery(new Term(FieldSource, s)), Occur.SHOULD);
        return boolQuery;
    }

    /// <summary>
    /// Wrap a content query with include/exclude source filters.
    /// Supports <see cref="SourceFilterSet"/> for ^ exclusion syntax, KB exclusion,
    /// and URL glob exclusions.
    /// </summary>
    private static Query ApplySourceFilter(Query contentQuery, IReadOnlyList<string>? sources)
    {
        var filterSet = SourceFilterSet.Parse(sources);
        if (!filterSet.HasFilters) return contentQuery;

        var boolQuery = new BooleanQuery { { contentQuery, Occur.MUST } };

        if (filterSet.ExcludeKbSources)
        {
            if (filterSet.Include.Count > 0)
            {
                // Combo mode (e.g., --name web,docs): Lucene can't express
                // "non-KB sources OR explicit KB includes OR web-validated".
                // Skip source filtering in Lucene entirely — SQLite is authoritative
                // for the combo case. Lucene still applies content matching + exclusions.
            }
            else
            {
                // Pure web mode: exclude KB source prefixes via PrefixQuery MUST_NOT.
                // Web-validated items are handled at the SQLite layer (which feeds
                // items into Lucene), so Lucene only sees already-eligible items.
                foreach (var prefix in SourceFilterSet.KbPrefixes)
                    boolQuery.Add(new PrefixQuery(new Term(FieldSource, prefix)), Occur.MUST_NOT);
                boolQuery.Add(new TermQuery(new Term(FieldSource, SourceFilterSet.ManualTag)), Occur.MUST_NOT);
            }
        }
        else
        {
            // Standard include: match any of these sources
            var includeFilter = BuildSourceIncludeFilter(filterSet.Include);
            if (includeFilter != null)
                boolQuery.Add(includeFilter, Occur.MUST);
        }

        // Exclude: must NOT match these source tags
        foreach (var excluded in filterSet.Exclude)
            boolQuery.Add(new TermQuery(new Term(FieldSource, excluded)), Occur.MUST_NOT);

        // URL glob exclusions
        foreach (var glob in filterSet.UrlExcludes)
            boolQuery.Add(
                new WildcardQuery(new Term(FieldUrl, glob.ToLowerInvariant())),
                Occur.MUST_NOT);

        return boolQuery;
    }

    /// <summary>
    /// Delete a document by ID.
    /// </summary>
    public void DeleteDocument(string id)
    {
        _writer?.DeleteDocuments(new Term(FieldId, id));
    }

    /// <summary>
    /// Delete all documents from the index.
    /// </summary>
    public void DeleteAll()
    {
        _writer?.DeleteAll();
        _writer?.Commit();
        RefreshReader();
    }

    private void RefreshReader()
    {
        lock (_lock)
        {
            var oldReader = _reader;
            _reader = _writer != null
                ? DirectoryReader.Open(_writer, applyAllDeletes: true)
                : DirectoryReader.Open(_directory);
            _searcher = new IndexSearcher(_reader);
            oldReader?.Dispose();
        }
    }

    private static string EscapeSpecialChars(string query)
    {
        // Don't escape: ~ (fuzzy), " (phrase), ^ (boost), : (field), AND/OR
        // Do escape: + - && || ! ( ) { } [ ] \ /
        var result = query
            .Replace("\\", "\\\\")
            .Replace("+", "\\+")
            .Replace("-", "\\-")
            .Replace("!", "\\!")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("/", "\\/");

        return result;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _analyzer.Dispose();
            _directory.Dispose();
        }
    }
}

/// <summary>
/// Search result from Lucene index.
/// </summary>
public record LuceneSearchResult
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Source { get; init; }
    public string? Url { get; init; }
    public float Score { get; init; }
}

/// <summary>
/// Generates optimized Lucene queries from natural language using a fast LLM call.
/// Leverages Lucene's advanced syntax: fuzzy (~), phrase (""), boosting (^), field-specific.
/// </summary>
public static partial class LuceneQueryGenerator
{
    // Source-generated regex for extracting backtick-delimited queries from verbose LLM responses
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex BacktickQueryRx();
    // Ultra-compact prompt for 0.6b class models (pipe-separated rules)
    private const string QueryGenerationPrompt =
        "NL→Lucene | ~=fuzzy | \"\"=phrase | title:^3=boost | htmx work?→title:htmx~^3 AND work~ | Query:";

    /// <summary>
    /// Generate an optimized Lucene query from natural language.
    /// Uses simple deterministic query building (0.6b models are unreliable for query syntax).
    /// LLM query generation can be re-enabled with useLlm=true for larger models.
    /// </summary>
    public static async Task<string> GenerateQueryAsync(
        string naturalLanguageQuery,
        OllamaService ollama,
        CancellationToken ct = default,
        bool useLlm = false)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguageQuery))
            return "";

        // Default: use simple deterministic query building (reliable, no prompt leaking)
        if (!useLlm)
            return BuildSimpleQuery(naturalLanguageQuery);

        // LLM-based query generation (only for larger models like 3b+)
        try
        {
            var prompt = $"{QueryGenerationPrompt} {naturalLanguageQuery} →";
            var result = await ollama.SentinelGenerateAsync(prompt, null, 0.1, ct);

            var query = result?.Trim() ?? "";

            // Remove markdown code blocks
            if (query.StartsWith("```"))
            {
                var endIndex = query.LastIndexOf("```");
                if (endIndex > 3)
                    query = query[3..endIndex].Trim();
            }

            // Extract query from backtick-delimited content in verbose responses
            if (query.Contains('`') && query.Length > 100)
            {
                var match = BacktickQueryRx().Match(query);
                if (match.Success)
                    query = match.Groups[1].Value.Trim();
            }

            // Extract line with query operators if multi-line
            if (query.Contains('\n'))
            {
                var firstLine = query.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.Contains('~') || l.Contains(':') || l.Contains('^'))
                    ?? query.Split('\n')[0];
                query = firstLine.Trim();
            }

            // Fall back to simple if still verbose (no query operators)
            if (query.Length > 100 && !query.Contains('~') && !query.Contains(':') && !query.Contains('^'))
                return BuildSimpleQuery(naturalLanguageQuery);

            if (string.IsNullOrWhiteSpace(query))
                return BuildSimpleQuery(naturalLanguageQuery);

            return query;
        }
        catch
        {
            return BuildSimpleQuery(naturalLanguageQuery);
        }
    }

    /// <summary>
    /// Build an optimized Lucene query without LLM assistance.
    /// Features: required primary term (+), title field boosting, fuzzy matching, phrase detection.
    /// Uses RelevanceScorer.Tokenize for stop word filtering.
    /// </summary>
    public static string BuildSimpleQuery(string naturalLanguageQuery)
    {
        // Get filtered tokens (stop words removed) via RelevanceScorer
        var filteredTokens = RelevanceScorer.Tokenize(naturalLanguageQuery);
        if (filteredTokens.Count == 0) return "";

        // Raw tokens preserve capitalization for phrase detection
        var rawTokens = naturalLanguageQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();

        var requiredTerms = new List<string>();  // MUST match (first important noun)
        var boostTerms = new List<string>();     // SHOULD match with boost
        var keyTerms = new List<string>();
        var filteredSet = new HashSet<string>(filteredTokens, StringComparer.OrdinalIgnoreCase);
        var foundPrimarySubject = false;
        var i = 0;

        while (i < rawTokens.Count)
        {
            var token = rawTokens[i];

            // Skip if this token was filtered as a stop word
            if (!filteredSet.Contains(token))
            {
                i++;
                continue;
            }

            // Check for potential phrase (consecutive capitalized words)
            if (i < rawTokens.Count - 1 && char.IsUpper(token[0]))
            {
                var phraseTokens = new List<string> { token };
                var j = i + 1;
                while (j < rawTokens.Count && char.IsUpper(rawTokens[j][0]))
                {
                    phraseTokens.Add(rawTokens[j]);
                    j++;
                }

                if (phraseTokens.Count > 1)
                {
                    var phrase = string.Join(" ", phraseTokens);
                    // Multi-word phrases are boosted but not required (could be context)
                    boostTerms.Add($"\"{phrase}\"^2");
                    keyTerms.Add(phrase);
                    i = j;
                    continue;
                }
            }

            // Detect technical terms (PascalCase, camelCase, acronyms, numbers)
            var isTechnical = char.IsUpper(token[0]) ||
                              token.Any(char.IsDigit) ||
                              token.Any(c => char.IsUpper(c) && token.IndexOf(c) > 0);

            // First non-technical content word is the PRIMARY SUBJECT - make it REQUIRED
            // This ensures "authentication" in "authentication for React" is mandatory
            if (!foundPrimarySubject && !isTechnical && token.Length >= 4)
            {
                foundPrimarySubject = true;
                var term = token.ToLowerInvariant();
                requiredTerms.Add($"+{term}");  // MUST match
                keyTerms.Add(term);
            }
            // Technical terms: exact match with boost, no fuzzy
            else if (isTechnical)
            {
                boostTerms.Add($"{token}^1.5");
                keyTerms.Add(token);
            }
            // Add fuzzy for longer non-technical terms
            else if (token.Length >= 4 && !token.Contains('.'))
            {
                boostTerms.Add($"{token.ToLowerInvariant()}~");
                keyTerms.Add(token.ToLowerInvariant());
            }
            else
            {
                boostTerms.Add(token.ToLowerInvariant());
            }

            i++;
        }

        // Add title-boosted versions of key terms (up to 3 most important)
        var titleBoosts = keyTerms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(t => $"title:{t}^3")
            .ToList();

        // Combine: required terms first, then boost terms, then title boosts
        var parts = new List<string>();
        parts.AddRange(requiredTerms);
        parts.AddRange(boostTerms);
        parts.AddRange(titleBoosts);

        return string.Join(" ", parts);
    }
}

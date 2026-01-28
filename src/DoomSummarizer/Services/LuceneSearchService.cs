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

            var luceneQuery = parser.Parse(EscapeSpecialChars(query));

            // Add source filter if specified
            if (!string.IsNullOrEmpty(sourceFilter))
            {
                var sourceQuery = new TermQuery(new Term(FieldSource, sourceFilter));
                var boolQuery = new BooleanQuery
                {
                    { luceneQuery, Occur.MUST },
                    { sourceQuery, Occur.MUST }
                };
                luceneQuery = boolQuery;
            }

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
            return SearchSimple(query, sourceFilter, limit);
        }
    }

    /// <summary>
    /// Simple search without query parsing (for fallback).
    /// </summary>
    private List<LuceneSearchResult> SearchSimple(string query, string? sourceFilter, int limit)
    {
        if (_searcher == null) return [];

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];

        var boolQuery = new BooleanQuery();
        foreach (var term in terms)
        {
            // Add fuzzy query for each term across all fields
            foreach (var field in new[] { FieldTitle, FieldKeywords, FieldContent })
            {
                var boost = FieldBoosts.GetValueOrDefault(field, 1.0f);
                var fuzzyQuery = new FuzzyQuery(new Term(field, term), 2) { Boost = boost };
                boolQuery.Add(fuzzyQuery, Occur.SHOULD);
            }
        }

        if (!string.IsNullOrEmpty(sourceFilter))
        {
            boolQuery.Add(new TermQuery(new Term(FieldSource, sourceFilter)), Occur.MUST);
        }

        var topDocs = _searcher.Search(boolQuery, limit);

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
    {
        // Enhance query with fuzzy matching for longer terms
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var enhanced = string.Join(" ", terms.Select(t =>
            t.Length >= 4 && !t.Contains('~') && !t.Contains('"') && !t.Contains(':')
                ? $"{t}~"
                : t));

        return Search(enhanced, sourceFilter, limit);
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
    /// Build a simple Lucene query without LLM assistance.
    /// Adds fuzzy matching to longer terms and phrase detection.
    /// </summary>
    public static string BuildSimpleQuery(string naturalLanguageQuery)
    {
        var tokens = naturalLanguageQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count == 0) return "";

        var parts = new List<string>();
        var i = 0;

        while (i < tokens.Count)
        {
            var token = tokens[i];

            // Check for potential phrase (consecutive capitalized words)
            if (i < tokens.Count - 1 && char.IsUpper(token[0]))
            {
                var phraseTokens = new List<string> { token };
                var j = i + 1;
                while (j < tokens.Count && char.IsUpper(tokens[j][0]))
                {
                    phraseTokens.Add(tokens[j]);
                    j++;
                }

                if (phraseTokens.Count > 1)
                {
                    parts.Add($"\"{string.Join(" ", phraseTokens)}\"");
                    i = j;
                    continue;
                }
            }

            // Add fuzzy for longer terms (likely to have typos or variants)
            if (token.Length >= 4 && !token.Contains('.'))
                parts.Add($"{token.ToLowerInvariant()}~");
            else
                parts.Add(token.ToLowerInvariant());

            i++;
        }

        return string.Join(" ", parts);
    }
}

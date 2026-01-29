using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Mostlylucid.DocSummarizer.Search;

namespace Mostlylucid.DocSummarizer.FullText.Lucene;

/// <summary>
/// Lucene.NET-based full-text search implementing <see cref="IFullTextSearch"/>.
/// File-based persistent index with advanced query syntax: fuzzy matching, phrase proximity, field boosting.
/// </summary>
public sealed class LuceneFullTextSearch : IFullTextSearch, IDisposable
{
    private const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;

    private const string FieldId = "id";
    private const string FieldTitle = "title";
    private const string FieldKeywords = "keywords";
    private const string FieldContent = "content";

    private static readonly Dictionary<string, float> FieldBoosts = new()
    {
        [FieldTitle] = 3.0f,
        [FieldKeywords] = 2.5f,
        [FieldContent] = 1.0f
    };

    private readonly FSDirectory _directory;
    private readonly StandardAnalyzer _analyzer;
    private IndexWriter? _writer;
    private DirectoryReader? _reader;
    private IndexSearcher? _searcher;
    private readonly object _lock = new();

    public string IndexPath { get; }

    public LuceneFullTextSearch(string indexPath)
    {
        IndexPath = indexPath;
        System.IO.Directory.CreateDirectory(indexPath);

        _directory = FSDirectory.Open(indexPath);
        _analyzer = new StandardAnalyzer(AppLuceneVersion);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_writer != null) return Task.CompletedTask;

            var config = new IndexWriterConfig(AppLuceneVersion, _analyzer)
            {
                OpenMode = OpenMode.CREATE_OR_APPEND
            };

            _writer = new IndexWriter(_directory, config);
            RefreshReader();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task IndexDocumentAsync(string id, string title, string content, IEnumerable<string>? keywords = null, CancellationToken ct = default)
    {
        if (_writer == null) throw new InvalidOperationException("Index not initialized. Call InitializeAsync first.");

        var keywordsText = keywords != null ? string.Join(" ", keywords) : "";

        var doc = new Document
        {
            new StringField(FieldId, id, Field.Store.YES),
            new TextField(FieldTitle, title ?? "", Field.Store.YES),
            new TextField(FieldKeywords, keywordsText, Field.Store.YES),
            new TextField(FieldContent, content ?? "", Field.Store.NO)
        };

        _writer.UpdateDocument(new Term(FieldId, id), doc);
        _writer.Commit();
        RefreshReader();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<List<FullTextResult>> SearchAsync(string query, int maxResults = 20, CancellationToken ct = default)
    {
        if (_searcher == null || string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new List<FullTextResult>());

        try
        {
            var parser = new MultiFieldQueryParser(
                AppLuceneVersion,
                [FieldTitle, FieldKeywords, FieldContent],
                _analyzer,
                FieldBoosts)
            {
                DefaultOperator = Operator.OR,
                AllowLeadingWildcard = false,
                FuzzyMinSim = 0.7f,
                PhraseSlop = 2
            };

            var luceneQuery = parser.Parse(EscapeSpecialChars(query));
            var topDocs = _searcher.Search(luceneQuery, maxResults);

            var results = topDocs.ScoreDocs.Select(sd =>
            {
                var doc = _searcher.Doc(sd.Doc);
                return new FullTextResult(
                    doc.Get(FieldId),
                    sd.Score,
                    doc.Get(FieldTitle));
            }).ToList();

            return Task.FromResult(results);
        }
        catch (ParseException)
        {
            return Task.FromResult(SearchSimple(query, maxResults));
        }
    }

    /// <inheritdoc/>
    public Task DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        _writer?.DeleteDocuments(new Term(FieldId, id));
        _writer?.Commit();
        RefreshReader();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        _writer?.DeleteAll();
        _writer?.Commit();
        RefreshReader();
        return Task.CompletedTask;
    }

    private List<FullTextResult> SearchSimple(string query, int limit)
    {
        if (_searcher == null) return [];

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];

        var boolQuery = new BooleanQuery();
        foreach (var term in terms)
        {
            foreach (var field in new[] { FieldTitle, FieldKeywords, FieldContent })
            {
                var boost = FieldBoosts.GetValueOrDefault(field, 1.0f);
                var fuzzyQuery = new FuzzyQuery(new Term(field, term), 2) { Boost = boost };
                boolQuery.Add(fuzzyQuery, Occur.SHOULD);
            }
        }

        var topDocs = _searcher.Search(boolQuery, limit);

        return topDocs.ScoreDocs.Select(sd =>
        {
            var doc = _searcher.Doc(sd.Doc);
            return new FullTextResult(
                doc.Get(FieldId),
                sd.Score,
                doc.Get(FieldTitle));
        }).ToList();
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
        return query
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

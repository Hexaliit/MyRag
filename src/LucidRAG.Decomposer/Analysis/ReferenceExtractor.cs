using System.Text.RegularExpressions;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
///     Extracts content references (URLs, file paths, DOIs) from the query.
///     Uses TextRecognizerService.ExtractUrls() for URL detection + additional patterns.
///     Each reference becomes a ContentReference node  -  fetched directly, not searched.
/// </summary>
public partial class ReferenceExtractor : IQueryAnalyzer
{
    private readonly ILogger<ReferenceExtractor>? _logger;

    public ReferenceExtractor(ILogger<ReferenceExtractor>? logger = null)
    {
        _logger = logger;
    }

    public Task<QuerySignals> AnalyzeAsync(string query, QuerySignals existing, CancellationToken ct = default)
    {
        var references = new List<ContentReference>(existing.References);
        var nodes = new List<QueryNode>(existing.ProposedNodes);
        var remainingQuery = query;

        // 1. Extract URLs (https://, http://)
        var urls = ExtractUrls(query);
        foreach (var url in urls)
        {
            var reference = new ContentReference
            {
                Uri = url,
                Kind = ContentReferenceKind.Url,
                OriginalText = url
            };
            references.Add(reference);
            remainingQuery = remainingQuery.Replace(url, "").Trim();

            nodes.Add(new QueryNode
            {
                Query = $"Analyze content from {url}",
                Type = QueryNodeType.ContentReference,
                Reference = reference,
                Depth = 0
            });
        }

        // 2. Extract DOIs (10.xxxx/yyyy)
        var dois = DoiPattern().Matches(query);
        foreach (Match doi in dois)
        {
            var doiText = doi.Value;
            var doiUri = doiText.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? doiText
                : $"https://doi.org/{doiText}";

            var reference = new ContentReference
            {
                Uri = doiUri,
                Kind = ContentReferenceKind.Doi,
                OriginalText = doiText
            };
            references.Add(reference);
            remainingQuery = remainingQuery.Replace(doiText, "").Trim();

            nodes.Add(new QueryNode
            {
                Query = $"Analyze paper {doiText}",
                Type = QueryNodeType.ContentReference,
                Reference = reference,
                Depth = 0
            });
        }

        // 3. Extract file paths (C:\..., /home/..., ~/...)
        var filePaths = FilePathPattern().Matches(query);
        foreach (Match fp in filePaths)
        {
            var path = fp.Value;
            var reference = new ContentReference
            {
                Uri = path,
                Kind = ContentReferenceKind.FilePath,
                OriginalText = path
            };
            references.Add(reference);
            remainingQuery = remainingQuery.Replace(path, "").Trim();

            nodes.Add(new QueryNode
            {
                Query = $"Analyze file {path}",
                Type = QueryNodeType.ContentReference,
                Reference = reference,
                Depth = 0
            });
        }

        // 4. Extract GitHub references (owner/repo#123)
        var ghRefs = GitHubRefPattern().Matches(query);
        foreach (Match gh in ghRefs)
        {
            var ghText = gh.Value;
            var reference = new ContentReference
            {
                Uri = $"https://github.com/{ghText.Replace("#", "/issues/")}",
                Kind = ContentReferenceKind.GitHubReference,
                OriginalText = ghText
            };
            references.Add(reference);
            remainingQuery = remainingQuery.Replace(ghText, "").Trim();

            nodes.Add(new QueryNode
            {
                Query = $"Analyze GitHub reference {ghText}",
                Type = QueryNodeType.ContentReference,
                Reference = reference,
                Depth = 0
            });
        }

        if (references.Count > existing.References.Count)
            _logger?.LogDebug("Extracted {Count} references from query: {Query}",
                references.Count - existing.References.Count, query);

        return Task.FromResult(existing with
        {
            References = references,
            ProposedNodes = nodes
        });
    }

    /// <summary>
    ///     Extract URLs from text. Uses a simple regex since the full
    ///     TextRecognizerService requires DoomSummarizer.Core reference.
    ///     When integrated, the adapter will use TextRecognizerService.ExtractUrls() instead.
    /// </summary>
    internal static List<string> ExtractUrls(string text)
    {
        var urls = new List<string>();
        var matches = UrlPattern().Matches(text);
        foreach (Match m in matches)
        {
            var url = m.Value.TrimEnd('.', ',', ')', ']', ';', ':', '!', '?');
            if (!urls.Contains(url))
                urls.Add(url);
        }

        return urls;
    }

    [GeneratedRegex(@"https?://[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:https?://doi\.org/)?10\.\d{4,}/[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex DoiPattern();

    [GeneratedRegex(@"(?:[A-Za-z]:\\[^\s<>""']+|/(?:home|usr|var|tmp|etc|opt)/[^\s<>""']+|~/[^\s<>""']+)",
        RegexOptions.None)]
    private static partial Regex FilePathPattern();

    [GeneratedRegex(@"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+#\d+", RegexOptions.None)]
    private static partial Regex GitHubRefPattern();
}
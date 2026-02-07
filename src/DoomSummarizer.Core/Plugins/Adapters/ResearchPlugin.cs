using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
///     Research-focused source plugin that extends arXiv fetching with citation resolution.
///     Fetches papers, extracts citations from full text (via ar5iv HTML), resolves DOIs/arXiv IDs,
///     and enriches ContentItems with citation metadata for the research-deep-dive template.
/// </summary>
public sealed class ResearchPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;
    private ICitationResolver? _citationResolver;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "research",
        Keys = ["research", "papers"],
        DisplayName = "Research Papers",
        Description =
            "Deep research paper analysis with citation resolution. Fetches full paper text and resolves citation networks.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.SubSource | SourceCapabilities.NoAuth,
        Examples =
        [
            "-s research", "-s \"research:attention mechanism\"", "-s research:2301.12345",
            "-s \"papers:retrieval augmented generation\""
        ]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        _citationResolver = new CitationResolver(_httpClient, NullLogger<CitationResolver>.Instance);
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        var items = new List<ContentItem>();
        var query = context.SubParams.Count > 0
            ? string.Join(" ", context.SubParams)
            : context.RawPrompt ?? "recent machine learning";

        // Check if query is a specific arXiv ID
        if (Regex.IsMatch(query, @"^\d{4}\.\d{4,5}(?:v\d+)?$"))
        {
            var paper = await FetchAndEnrichPaperAsync(query, ct);
            if (paper != null)
                items.Add(paper);
            return items;
        }

        // Search arXiv for papers
        var fetcher = new ArxivFetcher(_httpClient);
        var searchResults = await fetcher.SearchAsync(query, context.Limit);

        // Enrich top results with full text and citation metadata
        var maxEnrich = Math.Min(5, searchResults.Count);
        for (var i = 0; i < searchResults.Count; i++)
        {
            var item = searchResults[i];

            if (i < maxEnrich)
            {
                // Extract arXiv ID from URL
                var arxivId = AcademicPatterns.ExtractArxivIdFromUrl(item.Url);
                if (arxivId != null)
                {
                    var enriched = await EnrichWithCitationsAsync(item, arxivId, ct);
                    items.Add(enriched);
                    continue;
                }
            }

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    ///     Fetch a single paper by arXiv ID, get full text, and resolve citations.
    /// </summary>
    private async Task<ContentItem?> FetchAndEnrichPaperAsync(string arxivId, CancellationToken ct)
    {
        // Get metadata from arXiv API
        var metadata = await (_citationResolver?.ResolveArxivAsync(arxivId, ct)
                             ?? Task.FromResult<CitationMetadata?>(null));

        if (metadata == null)
            return null;

        var item = new ContentItem
        {
            Id = $"arxiv:{arxivId}",
            Source = "research",
            Title = metadata.Title,
            Url = $"https://arxiv.org/abs/{arxivId}",
            Content = metadata.Abstract ?? "",
            Author = string.Join(", ", metadata.Authors.Take(3)),
            CreatedAt = metadata.Year.HasValue
                ? new DateTimeOffset(metadata.Year.Value, 1, 1, 0, 0, 0, TimeSpan.Zero)
                : DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>
            {
                ["arxiv_id"] = arxivId,
                ["paper_type"] = "research_paper"
            }
        };

        return await EnrichWithCitationsAsync(item, arxivId, ct);
    }

    /// <summary>
    ///     Enrich a ContentItem with full paper text and resolved citation metadata.
    /// </summary>
    private async Task<ContentItem> EnrichWithCitationsAsync(ContentItem item, string arxivId, CancellationToken ct)
    {
        // Try to get full text via ar5iv
        var fullText = await AcademicPatterns.FetchAr5ivTextAsync(_httpClient, arxivId, ct);

        if (!string.IsNullOrEmpty(fullText))
        {
            // Use full text as content (truncated for embedding sanity)
            item.Content = fullText.Length > 50_000 ? fullText[..50_000] : fullText;
            item.IsEnriched = true;
        }

        // Extract citations from available text
        var textToAnalyze = fullText ?? item.Content ?? "";
        var citations = AcademicPatterns.ExtractCitationIds(textToAnalyze, arxivId);

        // Resolve up to 10 citations
        var resolved = new List<CitationMetadata>();
        var resolveCount = 0;
        foreach (var (citationType, citationId) in citations)
        {
            if (resolveCount >= 10) break;

            CitationMetadata? meta = null;
            if (citationType == "doi" && _citationResolver != null)
                meta = await _citationResolver.ResolveDoiAsync(citationId, ct);
            else if (citationType == "arxiv" && _citationResolver != null)
                meta = await _citationResolver.ResolveArxivAsync(citationId, ct);

            if (meta != null)
            {
                resolved.Add(meta);
                resolveCount++;
            }
        }

        // Build citation metadata dict
        var metadata = item.Metadata != null
            ? new Dictionary<string, string>(item.Metadata)
            : new Dictionary<string, string>();

        metadata["citation_count"] = citations.Count.ToString();
        metadata["resolved_citation_count"] = resolved.Count.ToString();
        metadata["arxiv_id"] = arxivId;
        metadata["paper_type"] = "research_paper";

        if (resolved.Count > 0)
        {
            // Store resolved citations as semicolon-separated summaries
            var citationSummaries = resolved
                .Select(r =>
                {
                    var authorStr = r.Authors.Count > 0 ? r.Authors[0] : "Unknown";
                    if (r.Authors.Count > 1) authorStr += " et al.";
                    var yearStr = r.Year?.ToString() ?? "n.d.";
                    return $"{authorStr} ({yearStr}). {r.Title}";
                })
                .ToList();

            metadata["resolved_citations"] = string.Join(" || ", citationSummaries);

            // Store shared references (DOIs/arXiv IDs that can be cross-referenced)
            var sharedDois = resolved.Where(r => r.Doi != null).Select(r => r.Doi!).ToList();
            if (sharedDois.Count > 0)
                metadata["cited_dois"] = string.Join(";", sharedDois);

            var sharedArxiv = resolved.Where(r => r.ArxivId != null).Select(r => r.ArxivId!).ToList();
            if (sharedArxiv.Count > 0)
                metadata["cited_arxiv_ids"] = string.Join(";", sharedArxiv);
        }

        return item with { Metadata = metadata };
    }
}

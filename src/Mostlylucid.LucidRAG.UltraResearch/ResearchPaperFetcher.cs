using System.Text;
using DoomSummarizer.Services;
using Microsoft.Extensions.Logging;

namespace LucidRAG.UltraResearch;

/// <summary>
///     Multi-source paper acquisition service. Wraps ArxivFetcher, CitationResolver,
///     and SemanticScholarClient to fetch papers and save them as .md files with YAML frontmatter.
/// </summary>
public class ResearchPaperFetcher
{
    private readonly ArxivFetcher _arxivFetcher;
    private readonly ICitationResolver _citationResolver;
    private readonly SemanticScholarClient _semanticScholar;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ResearchPaperFetcher> _logger;

    public ResearchPaperFetcher(
        ArxivFetcher arxivFetcher,
        ICitationResolver citationResolver,
        SemanticScholarClient semanticScholar,
        HttpClient httpClient,
        ILogger<ResearchPaperFetcher> logger)
    {
        _arxivFetcher = arxivFetcher;
        _citationResolver = citationResolver;
        _semanticScholar = semanticScholar;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    ///     Search arXiv and optionally Semantic Scholar for papers matching a query.
    ///     Returns new candidates not already in seenIds.
    /// </summary>
    public async Task<List<FetchCandidate>> SearchAsync(
        string query,
        UltraResearchConfig config,
        HashSet<string> seenIds,
        CancellationToken ct = default)
    {
        var candidates = new List<FetchCandidate>();

        // Search arXiv
        var categories = config.ArxivCategories;
        if (categories.Count > 0)
        {
            foreach (var category in categories)
            {
                var results = await _arxivFetcher.SearchAsync(query, maxResults: 20, category: category);
                AddArxivCandidates(results, seenIds, candidates, CandidateSource.Search);
            }
        }
        else
        {
            var results = await _arxivFetcher.SearchAsync(query, maxResults: 20);
            AddArxivCandidates(results, seenIds, candidates, CandidateSource.Search);
        }

        // Search Semantic Scholar
        if (config.IncludeSemanticScholar)
        {
            var s2Results = await _semanticScholar.SearchAsync(query, limit: 20, ct: ct);
            foreach (var paper in s2Results)
            {
                var (id, type) = NormalizePaperId(paper);
                if (id == null || !seenIds.Add(NormalizeSeenKey(type!, id))) continue;

                candidates.Add(new FetchCandidate
                {
                    Id = id,
                    Type = type!,
                    Source = CandidateSource.SemanticScholar,
                    Title = paper.Title,
                    CitedByCount = paper.CitationCount
                });
            }
        }

        _logger.LogDebug("Search '{Query}': {Count} new candidates", query, candidates.Count);
        return candidates;
    }

    /// <summary>
    ///     Fetch a paper, save it as .md, and return the file path.
    ///     Returns null if the paper could not be fetched.
    /// </summary>
    public async Task<FetchedPaper?> FetchAndPrepareAsync(
        FetchCandidate candidate,
        string dataDir,
        CancellationToken ct = default)
    {
        try
        {
            string? title = candidate.Title;
            string? content = null;
            List<string> authors = [];
            int? year = null;
            string? doi = null;
            string? arxivId = null;
            string? sourceUrl = null;

            if (candidate.Type == "arxiv")
            {
                arxivId = AcademicPatterns.StripArxivVersion(candidate.Id);
                sourceUrl = $"https://arxiv.org/abs/{arxivId}";

                // Resolve metadata via arXiv API
                var metadata = await _citationResolver.ResolveArxivAsync(arxivId, ct);
                if (metadata != null)
                {
                    title = metadata.Title;
                    authors = metadata.Authors;
                    year = metadata.Year;
                    doi = metadata.Doi;
                }

                // Try to get full text from ar5iv
                content = await AcademicPatterns.FetchAr5ivTextAsync(_httpClient, arxivId, ct);

                // Fallback: use abstract
                if (string.IsNullOrEmpty(content) && metadata?.Abstract != null)
                    content = metadata.Abstract;
            }
            else if (candidate.Type == "doi")
            {
                doi = AcademicPatterns.NormalizeDoi(candidate.Id);
                if (string.IsNullOrEmpty(doi)) doi = candidate.Id;
                sourceUrl = $"https://doi.org/{doi}";

                var metadata = await _citationResolver.ResolveDoiAsync(doi, ct);
                if (metadata != null)
                {
                    title = metadata.Title;
                    authors = metadata.Authors;
                    year = metadata.Year;
                    arxivId = metadata.ArxivId;
                    content = metadata.Abstract;
                }
            }

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
            {
                _logger.LogDebug("Could not fetch content for {Type}:{Id}", candidate.Type, candidate.Id);
                return null;
            }

            // Save as .md with YAML frontmatter
            var filePath = SaveAsMarkdown(dataDir, candidate, title, authors, year, doi, arxivId, sourceUrl, content);

            // Extract citation IDs from content for frontier expansion
            var citationIds = AcademicPatterns.ExtractCitationIds(content, arxivId);

            return new FetchedPaper(
                FilePath: filePath,
                Title: title,
                Authors: authors,
                Year: year,
                Doi: doi,
                ArxivId: arxivId,
                SourceUrl: sourceUrl,
                CitationIds: citationIds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Type}:{Id}", candidate.Type, candidate.Id);
            return null;
        }
    }

    /// <summary>
    ///     Get reverse citations (who cites this paper?) from Semantic Scholar.
    /// </summary>
    public async Task<List<FetchCandidate>> GetReverseCitationsAsync(
        FetchCandidate candidate,
        HashSet<string> seenIds,
        CancellationToken ct = default)
    {
        var s2Id = candidate.Type == "arxiv"
            ? SemanticScholarClient.ArxivToS2Id(candidate.Id)
            : SemanticScholarClient.DoiToS2Id(candidate.Id);

        var citations = await _semanticScholar.GetCitationsAsync(s2Id, limit: 50, ct: ct);
        var results = new List<FetchCandidate>();

        foreach (var citation in citations)
        {
            var (id, type) = NormalizePaperId(citation.Paper);
            if (id == null || !seenIds.Add(NormalizeSeenKey(type!, id))) continue;

            results.Add(new FetchCandidate
            {
                Id = id,
                Type = type!,
                Source = CandidateSource.SemanticScholar,
                Title = citation.Paper.Title,
                CitedByCount = citation.Paper.CitationCount,
                DiscoveredFrom = candidate.Id
            });
        }

        return results;
    }

    private string SaveAsMarkdown(
        string dataDir, FetchCandidate candidate,
        string title, List<string> authors, int? year,
        string? doi, string? arxivId, string? sourceUrl, string content)
    {
        var dir = Path.Combine(dataDir, "ultraresearch");
        Directory.CreateDirectory(dir);

        var sanitizedId = SanitizeFilename(candidate.Id);
        var filePath = Path.Combine(dir, $"{sanitizedId}.md");

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        if (authors.Count > 0)
            sb.AppendLine($"authors: \"{EscapeYaml(string.Join(", ", authors.Take(10)))}\"");
        if (year.HasValue)
            sb.AppendLine($"year: {year.Value}");
        if (!string.IsNullOrEmpty(doi))
            sb.AppendLine($"doi: \"{doi}\"");
        if (!string.IsNullOrEmpty(arxivId))
            sb.AppendLine($"arxiv_id: \"{arxivId}\"");
        if (!string.IsNullOrEmpty(sourceUrl))
            sb.AppendLine($"source_url: \"{sourceUrl}\"");
        sb.AppendLine($"fetched_at: \"{DateTimeOffset.UtcNow:O}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine(content);

        File.WriteAllText(filePath, sb.ToString());
        _logger.LogDebug("Saved paper to {Path}", filePath);
        return filePath;
    }

    private static void AddArxivCandidates(
        List<DoomSummarizer.Models.ContentItem> results,
        HashSet<string> seenIds,
        List<FetchCandidate> candidates,
        CandidateSource source)
    {
        foreach (var item in results)
        {
            var arxivId = ExtractArxivIdFromContentItem(item);
            if (arxivId == null) continue;

            var key = NormalizeSeenKey("arxiv", arxivId);
            if (!seenIds.Add(key)) continue;

            candidates.Add(new FetchCandidate
            {
                Id = arxivId,
                Type = "arxiv",
                Source = source,
                Title = item.Title
            });
        }
    }

    private static string? ExtractArxivIdFromContentItem(DoomSummarizer.Models.ContentItem item)
    {
        // Item.Id is like "arxiv_2301_12345"
        if (item.Id?.StartsWith("arxiv_") == true)
        {
            var rest = item.Id["arxiv_".Length..].Replace("_", ".");
            if (AcademicPatterns.ArxivBareIdRegex().IsMatch(rest))
                return rest;
        }

        // Try URL
        if (!string.IsNullOrEmpty(item.Url))
            return AcademicPatterns.ExtractArxivIdFromUrl(item.Url);

        return null;
    }

    public static (string? id, string? type) NormalizePaperId(S2Paper paper)
    {
        if (!string.IsNullOrEmpty(paper.ArxivId))
            return (AcademicPatterns.StripArxivVersion(paper.ArxivId), "arxiv");

        if (!string.IsNullOrEmpty(paper.Doi))
            return (paper.Doi, "doi");

        return (null, null);
    }

    public static string NormalizeSeenKey(string type, string id)
    {
        return type == "arxiv"
            ? $"arxiv:{AcademicPatterns.StripArxivVersion(id)}"
            : $"doi:{AcademicPatterns.NormalizeDoi(id)}";
    }

    private static string SanitizeFilename(string id)
    {
        return id.Replace("/", "_").Replace(":", "_").Replace(".", "_");
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
    }
}

/// <summary>
///     Result of fetching and preparing a paper.
/// </summary>
public record FetchedPaper(
    string FilePath,
    string Title,
    List<string> Authors,
    int? Year,
    string? Doi,
    string? ArxivId,
    string? SourceUrl,
    List<(string type, string id)> CitationIds);

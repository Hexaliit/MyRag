using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Fetches papers from arXiv API — free, no auth, Atom XML.
///     Returns paper titles and full abstracts as content for the pipeline.
///     Great for scientific/research queries with substantive content.
///     https://info.arxiv.org/help/api/basics.html
/// </summary>
public class ArxivFetcher(HttpClient httpClient)
{

    /// <summary>
    ///     arXiv category mappings for common topics.
    /// </summary>
    public static readonly Dictionary<string, string[]> TopicCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = ["cs.AI", "cs.LG", "cs.CL"],
        ["machine_learning"] = ["cs.LG", "stat.ML"],
        ["nlp"] = ["cs.CL"],
        ["computer_vision"] = ["cs.CV"],
        ["robotics"] = ["cs.RO"],
        ["security"] = ["cs.CR"],
        ["programming"] = ["cs.PL", "cs.SE"],
        ["science"] = ["physics", "q-bio", "math"],
        ["physics"] = ["physics"],
        ["biology"] = ["q-bio"],
        ["math"] = ["math"],
        ["quantum"] = ["quant-ph"],
        ["climate"] = ["physics.ao-ph"],
        ["health"] = ["q-bio", "cs.AI"]
    };

    /// <summary>
    ///     Search arXiv for papers matching a query. Returns full abstracts as content.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int maxResults = 15, string? category = null)
    {
        var items = new List<ContentItem>();

        try
        {
            // Build search query
            var searchQuery = $"all:{Uri.EscapeDataString(query)}";
            if (!string.IsNullOrEmpty(category))
                searchQuery = $"cat:{category}+AND+all:{Uri.EscapeDataString(query)}";

            // Sort by lastUpdatedDate to get recent papers — our RRF ranking
            // handles relevance via BM25 + embedding similarity
            var url =
                $"{AcademicPatterns.ArxivApiBaseUrl}?search_query={searchQuery}&max_results={maxResults}&sortBy=lastUpdatedDate&sortOrder=descending";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", AcademicPatterns.AcademicUserAgent);

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            foreach (var entry in doc.Descendants(AcademicPatterns.AtomNamespace +"entry").Take(maxResults))
            {
                var title = entry.Element(AcademicPatterns.AtomNamespace +"title")?.Value?.Trim();
                var summary = entry.Element(AcademicPatterns.AtomNamespace +"summary")?.Value?.Trim();
                var published = entry.Element(AcademicPatterns.AtomNamespace +"published")?.Value;
                var updated = entry.Element(AcademicPatterns.AtomNamespace +"updated")?.Value;
                var id = entry.Element(AcademicPatterns.AtomNamespace +"id")?.Value; // arXiv URL like http://arxiv.org/abs/2401.12345v1

                // Get authors
                var authors = entry.Elements(AcademicPatterns.AtomNamespace +"author")
                    .Select(a => a.Element(AcademicPatterns.AtomNamespace +"name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                // Get primary category
                var primaryCategory = entry.Element(AcademicPatterns.ArxivXmlNamespace +"primary_category")?.Attribute("term")?.Value;

                // Get PDF link
                var pdfLink = entry.Elements(AcademicPatterns.AtomNamespace +"link")
                    .FirstOrDefault(l => l.Attribute("title")?.Value == "pdf")
                    ?.Attribute("href")?.Value;

                // Get abstract page link
                var absLink = entry.Elements(AcademicPatterns.AtomNamespace +"link")
                    .FirstOrDefault(l => l.Attribute("type")?.Value == "text/html")
                    ?.Attribute("href")?.Value ?? id;

                if (string.IsNullOrEmpty(title)) continue;

                // Clean up whitespace in title and summary (arXiv adds line breaks)
                title = AcademicPatterns.CleanWhitespace(title);
                summary = summary != null ? AcademicPatterns.CleanWhitespace(summary) : "";

                // Build rich content: abstract + metadata
                var content = summary;
                if (authors.Count > 0)
                    content = $"Authors: {string.Join(", ", authors.Take(5))}{(authors.Count > 5 ? " et al." : "")}\n" +
                              $"Category: {primaryCategory ?? "unknown"}\n\n" +
                              content;

                var arxivId = id != null ? ExtractArxivIdFromEntry(id) : "";

                items.Add(new ContentItem
                {
                    Id =
                        $"arxiv_{(string.IsNullOrEmpty(arxivId) ? GenerateId(title) : arxivId.Replace(".", "_").Replace("/", "_"))}",
                    Source = "arxiv",
                    Title = title,
                    Url = absLink,
                    Content = content,
                    Author = authors.Count > 0
                        ? string.Join(", ", authors.Take(3)) + (authors.Count > 3 ? " et al." : "")
                        : null,
                    CreatedAt = TryParseDate(published ?? updated)
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: arXiv search failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Fetch recent papers from a specific arXiv category.
    /// </summary>
    public async Task<List<ContentItem>> FetchCategoryAsync(string category, int maxResults = 10)
    {
        var items = new List<ContentItem>();

        try
        {
            var url =
                $"{AcademicPatterns.ArxivApiBaseUrl}?search_query=cat:{category}&max_results={maxResults}&sortBy=submittedDate&sortOrder=descending";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", AcademicPatterns.AcademicUserAgent);

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            foreach (var entry in doc.Descendants(AcademicPatterns.AtomNamespace +"entry").Take(maxResults))
            {
                var title = entry.Element(AcademicPatterns.AtomNamespace +"title")?.Value?.Trim();
                var summary = entry.Element(AcademicPatterns.AtomNamespace +"summary")?.Value?.Trim();
                var published = entry.Element(AcademicPatterns.AtomNamespace +"published")?.Value;
                var id = entry.Element(AcademicPatterns.AtomNamespace +"id")?.Value;

                var authors = entry.Elements(AcademicPatterns.AtomNamespace +"author")
                    .Select(a => a.Element(AcademicPatterns.AtomNamespace +"name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                var absLink = entry.Elements(AcademicPatterns.AtomNamespace +"link")
                    .FirstOrDefault(l => l.Attribute("type")?.Value == "text/html")
                    ?.Attribute("href")?.Value ?? id;

                if (string.IsNullOrEmpty(title)) continue;

                title = AcademicPatterns.CleanWhitespace(title);
                summary = summary != null ? AcademicPatterns.CleanWhitespace(summary) : "";

                var arxivId = id != null ? ExtractArxivIdFromEntry(id) : "";

                items.Add(new ContentItem
                {
                    Id =
                        $"arxiv_{(string.IsNullOrEmpty(arxivId) ? GenerateId(title) : arxivId.Replace(".", "_").Replace("/", "_"))}",
                    Source = "arxiv",
                    Title = $"[arXiv:{category}] {title}",
                    Url = absLink,
                    Content =
                        $"Authors: {string.Join(", ", authors.Take(5))}{(authors.Count > 5 ? " et al." : "")}\n\n{summary}",
                    Author = authors.Count > 0
                        ? string.Join(", ", authors.Take(3)) + (authors.Count > 3 ? " et al." : "")
                        : null,
                    CreatedAt = TryParseDate(published)
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: arXiv category fetch failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Extract arXiv ID from an entry's id element URL.
    ///     Tries regex first, falls back to last path segment for old-format IDs.
    /// </summary>
    private static string ExtractArxivIdFromEntry(string url)
    {
        return AcademicPatterns.ExtractArxivIdFromUrl(url)
               ?? (url.LastIndexOf('/') is var idx && idx >= 0 ? url[(idx + 1)..] : url);
    }

    private static DateTimeOffset TryParseDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return DateTimeOffset.UtcNow;
        return DateTimeOffset.TryParse(dateStr, out var result) ? result : DateTimeOffset.UtcNow;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }
}
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DoomSummarizer.Models;
using Microsoft.Extensions.Logging;

namespace DoomSummarizer.Services;

/// <summary>
///     Resolves citation metadata from DOIs (CrossRef API) and arXiv IDs (arXiv API).
///     Results are cached in-memory since citation metadata doesn't change.
/// </summary>
public interface ICitationResolver
{
    Task<CitationMetadata?> ResolveDoiAsync(string doi, CancellationToken ct = default);
    Task<CitationMetadata?> ResolveArxivAsync(string arxivId, CancellationToken ct = default);
}

public partial class CitationResolver : ICitationResolver
{
    private const string CrossRefBaseUrl = "https://api.crossref.org/works/";
    private const string ArxivBaseUrl = "http://export.arxiv.org/api/query";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly ConcurrentDictionary<string, CitationMetadata?> _cache = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger<CitationResolver> _logger;

    // Rate limiting: last request time per API
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public CitationResolver(HttpClient httpClient, ILogger<CitationResolver> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Polite pool header for CrossRef
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "LucidRAG/1.0 (https://github.com/scottgal/lucidrag; mailto:scott@scottgal.com)");
    }

    public async Task<CitationMetadata?> ResolveDoiAsync(string doi, CancellationToken ct = default)
    {
        var cacheKey = $"doi:{doi}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        await RateLimitAsync(ct);

        try
        {
            var url = $"{CrossRefBaseUrl}{Uri.EscapeDataString(doi)}";
            var response = await _httpClient.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("CrossRef returned {Status} for DOI {Doi}", response.StatusCode, doi);
                _cache[cacheKey] = null;
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var work = doc.RootElement.GetProperty("message");

            var title = GetJsonString(work, "title");
            var authors = GetAuthors(work);
            var year = GetPublishedYear(work);
            var venue = GetJsonString(work, "container-title");
            var abstractText = GetJsonString(work, "abstract");
            var pdfUrl = GetPdfUrl(work);

            var metadata = new CitationMetadata(
                Title: title ?? $"DOI:{doi}",
                Authors: authors,
                Year: year,
                Venue: venue,
                Abstract: abstractText,
                Doi: doi,
                ArxivId: null,
                PdfUrl: pdfUrl);

            _cache[cacheKey] = metadata;
            _logger.LogDebug("Resolved DOI {Doi}: {Title}", doi, title);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve DOI {Doi}", doi);
            _cache[cacheKey] = null;
            return null;
        }
    }

    public async Task<CitationMetadata?> ResolveArxivAsync(string arxivId, CancellationToken ct = default)
    {
        var cacheKey = $"arxiv:{arxivId}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        await RateLimitAsync(ct);

        try
        {
            // Strip version suffix for API query
            var cleanId = arxivId.Contains('v')
                ? arxivId[..arxivId.IndexOf('v')]
                : arxivId;

            var url = $"{ArxivBaseUrl}?id_list={cleanId}";
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);

            var entry = doc.Descendants(Atom + "entry").FirstOrDefault();
            if (entry == null)
            {
                _logger.LogDebug("arXiv returned no entry for {ArxivId}", arxivId);
                _cache[cacheKey] = null;
                return null;
            }

            var title = CleanWhitespace(entry.Element(Atom + "title")?.Value?.Trim() ?? "");
            var summary = CleanWhitespace(entry.Element(Atom + "summary")?.Value?.Trim() ?? "");

            var authors = entry.Elements(Atom + "author")
                .Select(a => a.Element(Atom + "name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList();

            var published = entry.Element(Atom + "published")?.Value;
            int? year = null;
            if (DateTimeOffset.TryParse(published, out var pubDate))
                year = pubDate.Year;

            var pdfLink = entry.Elements(Atom + "link")
                .FirstOrDefault(l => l.Attribute("title")?.Value == "pdf")
                ?.Attribute("href")?.Value;

            // Extract DOI if present in entry
            var doiElement = entry.Elements(Atom + "link")
                .FirstOrDefault(l => l.Attribute("title")?.Value == "doi");
            var doi = doiElement?.Attribute("href")?.Value;

            var metadata = new CitationMetadata(
                Title: string.IsNullOrEmpty(title) ? $"arXiv:{arxivId}" : title,
                Authors: authors,
                Year: year,
                Venue: "arXiv",
                Abstract: summary,
                Doi: doi,
                ArxivId: arxivId,
                PdfUrl: pdfLink);

            _cache[cacheKey] = metadata;
            _logger.LogDebug("Resolved arXiv {ArxivId}: {Title}", arxivId, title);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve arXiv {ArxivId}", arxivId);
            _cache[cacheKey] = null;
            return null;
        }
    }

    private async Task RateLimitAsync(CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, ct);

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private static string? GetJsonString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Array => prop.EnumerateArray().FirstOrDefault().GetString(),
            _ => null
        };
    }

    private static List<string> GetAuthors(JsonElement work)
    {
        if (!work.TryGetProperty("author", out var authorArray))
            return [];

        return authorArray.EnumerateArray()
            .Select(a =>
            {
                var given = a.TryGetProperty("given", out var g) ? g.GetString() : "";
                var family = a.TryGetProperty("family", out var f) ? f.GetString() : "";
                return $"{given} {family}".Trim();
            })
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
    }

    private static int? GetPublishedYear(JsonElement work)
    {
        // Try "published-print", then "published-online", then "created"
        foreach (var field in new[] { "published-print", "published-online", "created" })
        {
            if (!work.TryGetProperty(field, out var dateField)) continue;
            if (!dateField.TryGetProperty("date-parts", out var dateParts)) continue;

            var parts = dateParts.EnumerateArray().FirstOrDefault();
            var yearEl = parts.EnumerateArray().FirstOrDefault();
            if (yearEl.ValueKind == JsonValueKind.Number)
                return yearEl.GetInt32();
        }

        return null;
    }

    private static string? GetPdfUrl(JsonElement work)
    {
        if (!work.TryGetProperty("link", out var links)) return null;

        foreach (var link in links.EnumerateArray())
        {
            var contentType = link.TryGetProperty("content-type", out var ct)
                ? ct.GetString()
                : null;
            if (contentType?.Contains("pdf") == true)
            {
                var url = link.TryGetProperty("URL", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(url)) return url;
            }
        }

        return null;
    }

    private static string CleanWhitespace(string text)
    {
        return WhitespaceRegex().Replace(text, " ").Trim();
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Discovers and parses RSS/Atom feeds from websites
/// </summary>
public partial class FeedDiscovery
{
    private readonly HttpClient _httpClient;

    public FeedDiscovery(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    ///     Attempts to discover and fetch content from a URL, trying feeds first
    /// </summary>
    public async Task<(List<ContentItem> items, string method)> FetchWithDiscoveryAsync(
        string url, int maxItems = 30, Action<string>? progress = null)
    {
        // Step 1: Check if URL is directly a feed
        progress?.Invoke($"Checking if {url} is a feed...");
        if (await TryParseFeedAsync(url) is { Count: > 0 } feedItems)
        {
            progress?.Invoke($"Found {feedItems.Count} items from RSS/Atom feed");
            return (feedItems.Take(maxItems).ToList(), "feed");
        }

        // Step 2: Try to discover feed links in the page
        progress?.Invoke("Discovering RSS/Atom feeds...");
        var feedUrls = await DiscoverFeedsAsync(url);

        foreach (var feedUrl in feedUrls)
        {
            progress?.Invoke($"Trying feed: {feedUrl}");
            if (await TryParseFeedAsync(feedUrl) is { Count: > 0 } discoveredItems)
            {
                progress?.Invoke($"Found {discoveredItems.Count} items from discovered feed");
                return (discoveredItems.Take(maxItems).ToList(), "discovered-feed");
            }
        }

        // Step 3: Try common feed URL patterns
        var commonFeedPaths = new[]
            { "/feed", "/rss", "/atom.xml", "/feed.xml", "/rss.xml", "/index.xml", "/feeds/posts/default" };
        var baseUri = new Uri(url);
        var baseUrl = $"{baseUri.Scheme}://{baseUri.Host}";

        foreach (var path in commonFeedPaths)
        {
            var feedUrl = baseUrl + path;
            try
            {
                if (await TryParseFeedAsync(feedUrl) is { Count: > 0 } patternItems)
                {
                    progress?.Invoke($"Found {patternItems.Count} items from common feed path: {path}");
                    return (patternItems.Take(maxItems).ToList(), "common-path-feed");
                }
            }
            catch
            {
                // Feed doesn't exist at this path
            }
        }

        // Step 4: Fall back to HTML extraction
        progress?.Invoke("No feeds found, falling back to HTML extraction...");
        return ([], "html-needed");
    }

    /// <summary>
    ///     Discovers RSS/Atom feed links from a webpage
    /// </summary>
    private async Task<List<string>> DiscoverFeedsAsync(string url)
    {
        var feeds = new List<string>();

        try
        {
            var response = await _httpClient.GetStringAsync(url);
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = await context.OpenAsync(req => req.Content(response));

            // Look for <link rel="alternate" type="application/rss+xml"> or atom+xml
            var feedLinks = document.QuerySelectorAll(
                "link[rel='alternate'][type='application/rss+xml'], " +
                "link[rel='alternate'][type='application/atom+xml'], " +
                "link[rel='alternate'][type='application/feed+json']");

            foreach (var link in feedLinks)
            {
                var href = link.GetAttribute("href");
                if (!string.IsNullOrEmpty(href))
                {
                    var feedUrl = ResolveUrl(url, href);
                    feeds.Add(feedUrl);
                }
            }

            // Also check for visible RSS links
            var rssLinks = document.QuerySelectorAll("a[href*='rss'], a[href*='feed'], a[href*='atom']");
            foreach (var link in rssLinks.Take(5))
            {
                var href = link.GetAttribute("href");
                if (!string.IsNullOrEmpty(href) && !feeds.Contains(href))
                {
                    var feedUrl = ResolveUrl(url, href);
                    if (feedUrl.EndsWith(".xml") || feedUrl.Contains("rss") || feedUrl.Contains("feed") ||
                        feedUrl.Contains("atom")) feeds.Add(feedUrl);
                }
            }
        }
        catch
        {
            // Failed to fetch page for discovery
        }

        return feeds.Distinct().Take(5).ToList();
    }

    /// <summary>
    ///     Tries to parse a URL as an RSS or Atom feed
    /// </summary>
    private async Task<List<ContentItem>?> TryParseFeedAsync(string feedUrl)
    {
        try
        {
            var response = await _httpClient.GetAsync(feedUrl);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(content)) return null;

            // Check if it looks like XML
            if (!content.TrimStart().StartsWith("<")) return null;

            var doc = XDocument.Parse(content);
            var root = doc.Root;
            if (root == null) return null;

            // RSS 2.0
            if (root.Name.LocalName == "rss") return ParseRss(doc, feedUrl);

            // Atom
            if (root.Name.LocalName == "feed") return ParseAtom(doc, feedUrl);

            // RSS 1.0 (RDF)
            if (root.Name.LocalName == "RDF") return ParseRdf(doc, feedUrl);
        }
        catch
        {
            // Not a valid feed
        }

        return null;
    }

    private static List<ContentItem> ParseRss(XDocument doc, string feedUrl)
    {
        var items = new List<ContentItem>();
        var channel = doc.Descendants("channel").FirstOrDefault();
        if (channel == null) return items;

        foreach (var item in channel.Descendants("item"))
        {
            var title = item.Element("title")?.Value?.Trim();
            var link = item.Element("link")?.Value?.Trim();
            var description = item.Element("description")?.Value?.Trim();
            var pubDate = item.Element("pubDate")?.Value;
            var guid = item.Element("guid")?.Value;

            if (string.IsNullOrEmpty(title)) continue;

            var cleanContent = CleanHtml(description);
            // Skip items where both title and content are empty/whitespace
            if (string.IsNullOrWhiteSpace(cleanContent) && string.IsNullOrWhiteSpace(title)) continue;

            var createdAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(pubDate) && DateTimeOffset.TryParse(pubDate, out var parsed)) createdAt = parsed;

            items.Add(new ContentItem
            {
                Id = $"feed_{GenerateId(guid ?? link ?? title)}",
                Source = "feed",
                Title = CleanHtml(title),
                Url = link ?? feedUrl,
                Content = string.IsNullOrWhiteSpace(cleanContent) ? null : cleanContent,
                CreatedAt = createdAt
            });
        }

        return items;
    }

    private static List<ContentItem> ParseAtom(XDocument doc, string feedUrl)
    {
        var items = new List<ContentItem>();
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        foreach (var entry in doc.Descendants(ns + "entry"))
        {
            var title = entry.Element(ns + "title")?.Value?.Trim();
            var link = entry.Elements(ns + "link")
                .FirstOrDefault(l => l.Attribute("rel")?.Value == "alternate" || l.Attribute("rel") == null)
                ?.Attribute("href")?.Value;
            var summary = entry.Element(ns + "summary")?.Value?.Trim()
                          ?? entry.Element(ns + "content")?.Value?.Trim();
            var updated = entry.Element(ns + "updated")?.Value
                          ?? entry.Element(ns + "published")?.Value;
            var id = entry.Element(ns + "id")?.Value;

            if (string.IsNullOrEmpty(title)) continue;

            var cleanContent = CleanHtml(summary);
            // Skip items where both title and content are empty/whitespace
            if (string.IsNullOrWhiteSpace(cleanContent) && string.IsNullOrWhiteSpace(title)) continue;

            var createdAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(updated) && DateTimeOffset.TryParse(updated, out var parsed)) createdAt = parsed;

            items.Add(new ContentItem
            {
                Id = $"feed_{GenerateId(id ?? link ?? title)}",
                Source = "feed",
                Title = CleanHtml(title),
                Url = link ?? feedUrl,
                Content = string.IsNullOrWhiteSpace(cleanContent) ? null : cleanContent,
                CreatedAt = createdAt
            });
        }

        return items;
    }

    private static List<ContentItem> ParseRdf(XDocument doc, string feedUrl)
    {
        var items = new List<ContentItem>();
        XNamespace rss = "http://purl.org/rss/1.0/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";

        foreach (var item in doc.Descendants(rss + "item"))
        {
            var title = item.Element(rss + "title")?.Value?.Trim();
            var link = item.Element(rss + "link")?.Value?.Trim();
            var description = item.Element(rss + "description")?.Value?.Trim();
            var date = item.Element(dc + "date")?.Value;

            if (string.IsNullOrEmpty(title)) continue;

            var createdAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(date) && DateTimeOffset.TryParse(date, out var parsed)) createdAt = parsed;

            items.Add(new ContentItem
            {
                Id = $"feed_{GenerateId(link ?? title)}",
                Source = "feed",
                Title = CleanHtml(title),
                Url = link ?? feedUrl,
                Content = CleanHtml(description),
                CreatedAt = createdAt
            });
        }

        return items;
    }

    private static string CleanHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Simple HTML tag removal
        var result = HtmlTagRegex().Replace(text, " ");
        result = WebUtility.HtmlDecode(result);
        result = WhitespaceRegex().Replace(result, " ");
        return result.Trim();
    }

    private static string ResolveUrl(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (Uri.TryCreate(new Uri(baseUrl), href, out var resolved))
            return resolved.ToString();

        return href;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input ?? ""))[..8]).ToLowerInvariant();
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
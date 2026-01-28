using System.Xml.Linq;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches news from various RSS-based news sources.
/// Supports BBC, The Guardian, Ars Technica, The Verge, and more.
/// </summary>
public class NewsFetcher(HttpClient httpClient)
{
    // Known news source RSS feeds
    private static readonly Dictionary<string, string[]> KnownFeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bbc"] = [
            "https://feeds.bbci.co.uk/news/technology/rss.xml",
            "https://feeds.bbci.co.uk/news/rss.xml"
        ],
        ["guardian"] = [
            "https://www.theguardian.com/technology/rss",
            "https://www.theguardian.com/world/rss"
        ],
        ["cnn"] = [
            "http://rss.cnn.com/rss/cnn_tech.rss",
            "http://rss.cnn.com/rss/cnn_topstories.rss"
        ],
        ["ars"] = ["https://feeds.arstechnica.com/arstechnica/index"],
        ["verge"] = ["https://www.theverge.com/rss/index.xml"],
        ["wired"] = ["https://www.wired.com/feed/rss"],
        ["techcrunch"] = ["https://techcrunch.com/feed/"],
        ["reuters"] = ["https://news.google.com/rss/search?q=when:24h+allinurl:reuters.com&ceid=US:en&hl=en-US&gl=US"],
        ["nytimes"] = ["https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml"],
        ["hackernoon"] = ["https://hackernoon.com/feed"],
        ["devto"] = ["https://dev.to/feed"],
        ["lobsters"] = ["https://lobste.rs/rss"],
        ["slashdot"] = ["https://rss.slashdot.org/Slashdot/slashdotMain"],
        ["engadget"] = ["https://www.engadget.com/rss.xml"],
        ["zdnet"] = ["https://www.zdnet.com/news/rss.xml"],
        ["thenextweb"] = ["https://thenextweb.com/feed"],
        ["mostlylucid"] = ["https://www.mostlylucid.net/rss"],
        ["npr"] = ["https://feeds.npr.org/1001/rss.xml"],
        ["theregister"] = ["https://www.theregister.com/headlines.atom"],
        ["sciencedaily"] = ["https://www.sciencedaily.com/rss/all.xml"],
        ["phys"] = ["https://phys.org/rss-feed/"],
        ["carbonbrief"] = ["https://www.carbonbrief.org/feed/"],
        ["theonion"] = ["https://theonion.com/feed/"],
        ["babylonbee"] = ["https://babylonbee.com/feed"]
    };

    // Sources with dedicated search APIs (prefer these over RSS+filter)
    private static readonly Dictionary<string, string> SearchApis = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mostlylucid"] = "https://www.mostlylucid.net/api/typeahead/"
    };

    /// <summary>
    /// Fetch from a known news source by name.
    /// Supports category-specific feeds from sources.yaml routing.
    /// </summary>
    public async Task<List<ContentItem>> FetchSourceAsync(string sourceName, int limit = 25, string? query = null)
    {
        // Try API-based search first if query provided and source has API
        if (!string.IsNullOrEmpty(query) && SearchApis.TryGetValue(sourceName, out var apiUrl))
        {
            var apiItems = await FetchFromSearchApiAsync(sourceName, apiUrl, query, limit);
            if (apiItems.Count > 0) return apiItems;
        }

        // Try SourceRouter (YAML-defined feeds) first — category-specific or default
        try
        {
            var router = SourceRouter.Load();
            var category = !string.IsNullOrEmpty(query) ? query.ToLowerInvariant() : null;
            var yamlFeeds = router.GetFeeds(sourceName, category);
            if (yamlFeeds.Count > 0)
            {
                var items = new List<ContentItem>();
                foreach (var feedUrl in yamlFeeds)
                {
                    var feedItems = await FetchRssAsync(feedUrl, sourceName, limit);
                    items.AddRange(feedItems);
                    if (items.Count >= limit) break;
                }
                if (items.Count > 0)
                    return items.Take(limit).ToList();
            }
        }
        catch
        {
            // Fall through to hardcoded feeds
        }

        // Fallback to hardcoded KnownFeeds
        if (!KnownFeeds.TryGetValue(sourceName, out var feeds))
        {
            System.Diagnostics.Debug.WriteLine($"Unknown news source: {sourceName}. Known: {string.Join(", ", KnownFeeds.Keys)}");
            return [];
        }

        var allItems = new List<ContentItem>();

        foreach (var feedUrl in feeds)
        {
            try
            {
                var feedItems = await FetchRssAsync(feedUrl, sourceName, limit);

                // Filter by query if provided (fallback when no API or category feed)
                if (!string.IsNullOrEmpty(query))
                {
                    var queryLower = query.ToLowerInvariant();
                    feedItems = feedItems
                        .Where(i =>
                            (i.Title?.ToLowerInvariant().Contains(queryLower) ?? false) ||
                            (i.Content?.ToLowerInvariant().Contains(queryLower) ?? false))
                        .ToList();
                }

                allItems.AddRange(feedItems);

                if (allItems.Count >= limit) break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to fetch {feedUrl}: {ex.Message}");
            }
        }

        return allItems.Take(limit).ToList();
    }

    /// <summary>
    /// Fetch from a search API (like mostlylucid typeahead).
    /// </summary>
    private async Task<List<ContentItem>> FetchFromSearchApiAsync(string sourceName, string apiBaseUrl, string query, int limit)
    {
        var items = new List<ContentItem>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{apiBaseUrl}{encodedQuery}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");
            request.Headers.Add("Accept", "application/json");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var results = System.Text.Json.JsonSerializer.Deserialize<List<TypeaheadResult>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (results != null)
            {
                foreach (var result in results.Take(limit))
                {
                    items.Add(new ContentItem
                    {
                        Id = $"{sourceName}_{result.Slug?.GetHashCode() ?? result.Title?.GetHashCode() ?? 0}",
                        Source = sourceName,
                        Title = result.Title ?? "Untitled",
                        Url = result.Url,
                        Score = (int)(result.Score * 100), // Convert 0-1 to 0-100
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: API search failed for {sourceName}: {ex.Message}");
        }

        return items;
    }

    private record TypeaheadResult(string? Title, string? Slug, string? Url, double Score);

    /// <summary>
    /// Fetch from a raw RSS URL.
    /// </summary>
    public async Task<List<ContentItem>> FetchRssAsync(string feedUrl, string sourceName, int limit = 25)
    {
        var items = new List<ContentItem>();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            // Try RSS 2.0 format
            var rssItems = doc.Descendants("item");
            foreach (var item in rssItems.Take(limit))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var description = item.Element("description")?.Value;
                var pubDate = item.Element("pubDate")?.Value;
                var creator = item.Element(XName.Get("creator", "http://purl.org/dc/elements/1.1/"))?.Value;

                // Try to get image from enclosure or media:content
                var imageUrl = item.Element("enclosure")?.Attribute("url")?.Value;
                if (string.IsNullOrEmpty(imageUrl))
                {
                    imageUrl = item.Element(XName.Get("thumbnail", "http://search.yahoo.com/mrss/"))?.Attribute("url")?.Value;
                }
                if (string.IsNullOrEmpty(imageUrl))
                {
                    imageUrl = item.Element(XName.Get("content", "http://search.yahoo.com/mrss/"))?.Attribute("url")?.Value;
                }

                if (!string.IsNullOrEmpty(title))
                {
                    items.Add(new ContentItem
                    {
                        Id = $"{sourceName}_{link?.GetHashCode() ?? title.GetHashCode()}",
                        Source = sourceName,
                        Title = System.Net.WebUtility.HtmlDecode(title),
                        Url = link,
                        Content = StripHtml(description ?? ""),
                        Author = creator,
                        CreatedAt = TryParseDate(pubDate),
                        ImageUrl = imageUrl
                    });
                }
            }

            // If no RSS items, try Atom format
            if (items.Count == 0)
            {
                var ns = XNamespace.Get("http://www.w3.org/2005/Atom");
                var atomEntries = doc.Descendants(ns + "entry");

                foreach (var entry in atomEntries.Take(limit))
                {
                    var title = entry.Element(ns + "title")?.Value;
                    var link = entry.Element(ns + "link")?.Attribute("href")?.Value;
                    var summary = entry.Element(ns + "summary")?.Value ?? entry.Element(ns + "content")?.Value;
                    var updated = entry.Element(ns + "updated")?.Value;
                    var author = entry.Element(ns + "author")?.Element(ns + "name")?.Value;

                    if (!string.IsNullOrEmpty(title))
                    {
                        items.Add(new ContentItem
                        {
                            Id = $"{sourceName}_{link?.GetHashCode() ?? title.GetHashCode()}",
                            Source = sourceName,
                            Title = System.Net.WebUtility.HtmlDecode(title),
                            Url = link,
                            Content = StripHtml(summary ?? ""),
                            Author = author,
                            CreatedAt = TryParseDate(updated)
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: RSS parse error for {feedUrl}: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// List known news sources.
    /// </summary>
    public static IEnumerable<string> KnownSources => KnownFeeds.Keys;

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 1500 ? text[..1500] : text;
    }

    private static DateTimeOffset TryParseDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return DateTimeOffset.UtcNow;

        if (DateTimeOffset.TryParse(dateStr, out var result))
            return result;

        return DateTimeOffset.UtcNow;
    }
}

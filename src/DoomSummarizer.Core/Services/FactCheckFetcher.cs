using System.Text.RegularExpressions;
using System.Xml.Linq;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches from fact-checking RSS feeds: Snopes, PolitiFact, FactCheck.org, FullFact.
/// No API keys required. Great for verifying claims and tracking misinformation.
/// </summary>
public partial class FactCheckFetcher(HttpClient httpClient)
{
    private static readonly Dictionary<string, string> Feeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["snopes"] = "https://www.snopes.com/feed/",
        ["politifact"] = "https://www.politifact.com/rss/all/",
        ["factcheck"] = "https://www.factcheck.org/feed/",
        ["fullfact"] = "https://fullfact.org/feed/",
    };

    /// <summary>
    /// Fetch recent fact-checks from all configured sources.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? site = null)
    {
        var items = new List<ContentItem>();
        var feeds = site != null && Feeds.TryGetValue(site, out var singleFeed)
            ? new Dictionary<string, string> { [site] = singleFeed }
            : Feeds;

        var perFeedLimit = Math.Max(5, limit / feeds.Count);

        var tasks = feeds.Select(kv => FetchFeedAsync(kv.Key, kv.Value, perFeedLimit));
        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
            items.AddRange(result);

        return items
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToList();
    }

    private async Task<List<ContentItem>> FetchFeedAsync(string siteName, string feedUrl, int limit)
    {
        var items = new List<ContentItem>();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");
            request.Headers.Add("Accept", "application/rss+xml, application/xml, text/xml");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            foreach (var item in doc.Descendants("item").Take(limit))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var description = item.Element("description")?.Value;
                var pubDate = item.Element("pubDate")?.Value;
                var creator = item.Element(XName.Get("creator", "http://purl.org/dc/elements/1.1/"))?.Value;

                if (string.IsNullOrEmpty(title)) continue;

                items.Add(new ContentItem
                {
                    Id = $"factcheck_{siteName}_{GenerateId(link ?? title)}",
                    Source = "factcheck",
                    Title = System.Net.WebUtility.HtmlDecode(title),
                    Url = link,
                    Content = StripHtml(description ?? ""),
                    Author = $"{siteName}: {creator ?? siteName}",
                    CreatedAt = TryParseDate(pubDate)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Fact-check feed '{siteName}' failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Available fact-checking sites.
    /// </summary>
    public static IEnumerable<string> Sites => Feeds.Keys;

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > 1500 ? text[..1500] : text;
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static DateTimeOffset TryParseDate(string? dateStr) =>
        string.IsNullOrEmpty(dateStr) ? DateTimeOffset.UtcNow
            : DateTimeOffset.TryParse(dateStr, out var r) ? r : DateTimeOffset.UtcNow;

    private static string GenerateId(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
}

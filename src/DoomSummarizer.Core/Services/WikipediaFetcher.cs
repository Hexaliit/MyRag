using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Fetches content from Wikipedia using the proper MediaWiki Action API for search
///     and the REST API for article summaries and featured content.
///     No API key required. Follows Wikimedia User-Agent policy.
///     https://www.mediawiki.org/wiki/API:Main_page
///     https://en.wikipedia.org/api/rest_v1/
/// </summary>
public partial class WikipediaFetcher(HttpClient httpClient)
{
    private const string ActionApiUrl = "https://en.wikipedia.org/w/api.php";
    private const string RestApiUrl = "https://en.wikipedia.org/api/rest_v1";

    // Wikimedia requires a descriptive User-Agent with contact info
    // https://meta.wikimedia.org/wiki/User-Agent_policy
    internal const string UserAgent =
        "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag; scott@mostlylucid.net)";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    ///     Search Wikipedia articles matching a query using the MediaWiki Action API.
    ///     For each result, fetches a clean summary via the REST page/summary endpoint.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int limit = 10)
    {
        var items = new List<ContentItem>();

        try
        {
            // Step 1: Search via MediaWiki Action API (opensearch or query&list=search)
            var searchUrl = $"{ActionApiUrl}?action=query&list=search" +
                            $"&srsearch={Uri.EscapeDataString(query)}" +
                            $"&srlimit={Math.Min(limit, 20)}" +
                            "&srinfo=totalhits" +
                            "&srprop=snippet|titlesnippet|sectionsnippet|wordcount" +
                            "&format=json&formatversion=2";

            var searchResult = await FetchJsonAsync<MediaWikiSearchResponse>(searchUrl);
            if (searchResult?.Query?.Search == null || searchResult.Query.Search.Count == 0)
                return items;

            // Step 2: For each search result, get a clean summary via REST API
            foreach (var result in searchResult.Query.Search.Take(limit))
            {
                if (string.IsNullOrEmpty(result.Title)) continue;

                // Rate limit: 200ms between REST calls to be polite
                if (items.Count > 0)
                    await Task.Delay(200);

                try
                {
                    var summary = await GetArticleSummaryAsync(result.Title);
                    if (summary == null) continue;

                    items.Add(new ContentItem
                    {
                        Id =
                            $"wiki_{(summary.PageId.HasValue ? summary.PageId.Value.ToString() : GenerateId(result.Title))}",
                        Source = "wikipedia",
                        Title = summary.DisplayTitle ?? summary.Title ?? result.Title,
                        Url = summary.ContentUrls?.Desktop?.Page
                              ??
                              $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(result.Title.Replace(' ', '_'))}",
                        Content = summary.Extract ?? StripHtml(result.Snippet ?? ""),
                        Author = "Wikipedia",
                        CreatedAt = TryParseDate(summary.Timestamp),
                        Tags = summary.Description != null ? [summary.Description] : []
                    });
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Article doesn't exist in REST API (rare for search results)
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Wikipedia search failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Get a clean article summary via the REST API page/summary endpoint.
    ///     Returns structured data: title, extract (plain text), thumbnail, description.
    /// </summary>
    public async Task<WikiSummary?> GetArticleSummaryAsync(string title)
    {
        var encodedTitle = Uri.EscapeDataString(title.Replace(' ', '_'));
        var url = $"{RestApiUrl}/page/summary/{encodedTitle}";

        return await FetchJsonAsync<WikiSummary>(url);
    }

    /// <summary>
    ///     Fetch today's featured content (includes "In the news" items).
    ///     Retained for browsing mode when no search query is provided.
    /// </summary>
    public async Task<List<ContentItem>> FetchFeaturedAsync(int limit = 20, string? section = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var today = DateTime.UtcNow;
            var url = $"{RestApiUrl}/feed/featured/{today:yyyy}/{today:MM}/{today:dd}";

            var featured = await FetchJsonAsync<WikiFeatured>(url);
            if (featured == null) return items;

            // "In the news" items -- current events
            if (section is null or "news" && featured.News != null)
                foreach (var news in featured.News.Take(limit))
                {
                    var story = news.Story;
                    if (string.IsNullOrEmpty(story)) continue;

                    var links = news.Links ?? [];
                    var primaryLink = links.FirstOrDefault();
                    var articleUrl = primaryLink?.ContentUrls?.Desktop?.Page;
                    var title = primaryLink?.NormalizedTitle ?? primaryLink?.Title ?? StripHtml(story);

                    if (title.Length > 200) title = title[..197] + "...";

                    items.Add(new ContentItem
                    {
                        Id = $"wiki_news_{GenerateId(story)}",
                        Source = "wikipedia",
                        Title = title,
                        Url = articleUrl ?? "https://en.wikipedia.org/wiki/Portal:Current_events",
                        Content = StripHtml(story),
                        Author = "Wikipedia",
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }

            // "On this day" -- historical events
            if (section is null or "history" && featured.OnThisDay != null)
                foreach (var otd in featured.OnThisDay.Take(Math.Max(3, limit - items.Count)))
                {
                    if (string.IsNullOrEmpty(otd.Text)) continue;

                    var pages = otd.Pages ?? [];
                    var primaryPage = pages.FirstOrDefault();
                    var articleUrl = primaryPage?.ContentUrls?.Desktop?.Page;

                    items.Add(new ContentItem
                    {
                        Id = $"wiki_otd_{GenerateId(otd.Text)}",
                        Source = "wikipedia",
                        Title =
                            $"[On this day, {otd.Year}] {(otd.Text.Length > 150 ? otd.Text[..147] + "..." : otd.Text)}",
                        Url = articleUrl ?? "https://en.wikipedia.org/wiki/Wikipedia:On_this_day",
                        Content = otd.Text,
                        Author = "Wikipedia",
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }

            // Featured article of the day
            if (section is null or "featured" && featured.Tfa != null)
            {
                var tfa = featured.Tfa;
                var articleUrl = tfa.ContentUrls?.Desktop?.Page;
                items.Add(new ContentItem
                {
                    Id = $"wiki_tfa_{GenerateId(tfa.Title ?? "tfa")}",
                    Source = "wikipedia",
                    Title = $"[Featured] {tfa.NormalizedTitle ?? tfa.Title ?? "Today's Featured Article"}",
                    Url = articleUrl ?? "https://en.wikipedia.org/wiki/Main_Page",
                    Content = tfa.Extract ?? "",
                    Author = "Wikipedia",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Wikipedia featured API failed: {ex.Message}");
        }

        return items.Take(limit).ToList();
    }

    /// <summary>
    ///     Original FetchAsync: routes to search if a query is embedded, otherwise featured content.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? section = null, string? query = null)
    {
        // If a search query is provided, use the proper search API
        if (!string.IsNullOrWhiteSpace(query))
            return await SearchAsync(query, limit);

        // Otherwise browse featured content
        return await FetchFeaturedAsync(limit, section);
    }

    private async Task<T?> FetchJsonAsync<T>(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        request.Headers.Add("Accept", "application/json");

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > 1500 ? text[..1500] : text;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }

    private static DateTimeOffset TryParseDate(string? dateStr)
    {
        return string.IsNullOrEmpty(dateStr) ? DateTimeOffset.UtcNow
            : DateTimeOffset.TryParse(dateStr, out var r) ? r : DateTimeOffset.UtcNow;
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ── MediaWiki Action API response models ──────────────────────────

    private record MediaWikiSearchResponse(MediaWikiQuery? Query);

    private record MediaWikiQuery(
        [property: JsonPropertyName("search")] List<MediaWikiSearchResult>? Search);

    private record MediaWikiSearchResult(
        string? Title,
        [property: JsonPropertyName("pageid")] long? PageId,
        string? Snippet,
        [property: JsonPropertyName("wordcount")]
        int? WordCount);

    // ── REST API page/summary model ───────────────────────────────────

    /// <summary>REST API /page/summary response.</summary>
    public record WikiSummary(
        string? Title,
        [property: JsonPropertyName("displaytitle")]
        string? DisplayTitle,
        [property: JsonPropertyName("pageid")] long? PageId,
        string? Extract,
        string? Description,
        string? Timestamp,
        [property: JsonPropertyName("content_urls")]
        WikiContentUrls? ContentUrls,
        WikiThumbnail? Thumbnail);

    public record WikiThumbnail(string? Source, int? Width, int? Height);

    // ── Wikimedia REST feed/featured models ────────────────────────────

    private record WikiFeatured(
        WikiArticle? Tfa,
        List<WikiNewsItem>? News,
        [property: JsonPropertyName("onthisday")]
        List<WikiOnThisDay>? OnThisDay);

    private record WikiNewsItem(string? Story, List<WikiArticle>? Links);

    private record WikiOnThisDay(string? Text, int? Year, List<WikiArticle>? Pages);

    private record WikiArticle(
        string? Title,
        [property: JsonPropertyName("normalizedtitle")]
        string? NormalizedTitle,
        string? Extract,
        [property: JsonPropertyName("content_urls")]
        WikiContentUrls? ContentUrls);

    public record WikiContentUrls(WikiPlatformUrls? Desktop, WikiPlatformUrls? Mobile);

    public record WikiPlatformUrls(string? Page);
}
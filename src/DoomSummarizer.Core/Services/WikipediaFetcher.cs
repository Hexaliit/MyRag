using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches current events and featured content from Wikipedia/Wikimedia REST API.
/// No API key required. Covers "In the news", "On this day", and featured articles.
/// https://en.wikipedia.org/api/rest_v1/
/// </summary>
public partial class WikipediaFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://en.wikipedia.org/api/rest_v1";

    /// <summary>
    /// Fetch today's featured content (includes "In the news" items).
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? section = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var today = DateTime.UtcNow;
            var url = $"{BaseUrl}/feed/featured/{today:yyyy}/{today:MM}/{today:dd}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0 (news aggregator; https://github.com)");
            request.Headers.Add("Accept", "application/json");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var featured = JsonSerializer.Deserialize<WikiFeatured>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (featured == null) return items;

            // "In the news" items — current events
            if ((section == null || section == "news") && featured.News != null)
            {
                foreach (var news in featured.News.Take(limit))
                {
                    var story = news.Story;
                    if (string.IsNullOrEmpty(story)) continue;

                    // Extract linked articles from the news item
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
                        Url = articleUrl ?? $"https://en.wikipedia.org/wiki/Portal:Current_events",
                        Content = StripHtml(story),
                        Author = "Wikipedia",
                        CreatedAt = DateTimeOffset.UtcNow // Wikipedia doesn't timestamp news items
                    });
                }
            }

            // "On this day" — historical events
            if ((section == null || section == "history") && featured.OnThisDay != null)
            {
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
                        Title = $"[On this day, {otd.Year}] {(otd.Text.Length > 150 ? otd.Text[..147] + "..." : otd.Text)}",
                        Url = articleUrl ?? "https://en.wikipedia.org/wiki/Wikipedia:On_this_day",
                        Content = otd.Text,
                        Author = "Wikipedia",
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            // Featured article of the day
            if ((section == null || section == "featured") && featured.Tfa != null)
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
            System.Diagnostics.Debug.WriteLine($"Warning: Wikipedia API failed: {ex.Message}");
        }

        return items.Take(limit).ToList();
    }

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > 1500 ? text[..1500] : text;
    }

    private static string GenerateId(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Wikimedia REST API response models
    private record WikiFeatured(
        WikiArticle? Tfa, // Today's Featured Article
        List<WikiNewsItem>? News,
        [property: JsonPropertyName("onthisday")] List<WikiOnThisDay>? OnThisDay);

    private record WikiNewsItem(string? Story, List<WikiArticle>? Links);
    private record WikiOnThisDay(string? Text, int? Year, List<WikiArticle>? Pages);

    private record WikiArticle(
        string? Title,
        [property: JsonPropertyName("normalizedtitle")] string? NormalizedTitle,
        string? Extract,
        [property: JsonPropertyName("content_urls")] WikiContentUrls? ContentUrls);

    private record WikiContentUrls(WikiPlatformUrls? Desktop, WikiPlatformUrls? Mobile);
    private record WikiPlatformUrls(string? Page);
}

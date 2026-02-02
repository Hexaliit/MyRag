using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches from the Spaceflight News API (SNAPI) — free, no auth, REST JSON.
/// Covers launches, events, and articles from NASA, ESA, SpaceX, etc.
/// https://api.spaceflightnewsapi.net/v4/docs
/// </summary>
public class SpaceflightNewsFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://api.spaceflightnewsapi.net/v4";

    /// <summary>
    /// Fetch recent spaceflight news articles.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? search = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/articles/?limit={Math.Min(limit, 40)}&ordering=-published_at";
            if (!string.IsNullOrEmpty(search))
                url += $"&search={Uri.EscapeDataString(search)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<SnapiResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Results == null) return items;

            foreach (var article in result.Results.Take(limit))
            {
                if (string.IsNullOrEmpty(article.Title)) continue;

                items.Add(new ContentItem
                {
                    Id = $"space_{article.Id}",
                    Source = "spaceflight",
                    Title = article.Title,
                    Url = article.Url,
                    Content = article.Summary ?? "",
                    Author = article.NewsSite,
                    ImageUrl = article.ImageUrl,
                    CreatedAt = article.PublishedAt ?? DateTimeOffset.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Spaceflight News API failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Fetch upcoming and recent spaceflight events (launches, landings, docking).
    /// </summary>
    public async Task<List<ContentItem>> FetchEventsAsync(int limit = 10)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/events/?limit={Math.Min(limit, 20)}&ordering=-date";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<SnapiEventResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Results == null) return items;

            foreach (var evt in result.Results.Take(limit))
            {
                if (string.IsNullOrEmpty(evt.Title)) continue;

                items.Add(new ContentItem
                {
                    Id = $"space_evt_{evt.Id}",
                    Source = "spaceflight",
                    Title = $"[Event] {evt.Title}",
                    Url = evt.Url,
                    Content = evt.Description ?? "",
                    Author = evt.Provider,
                    CreatedAt = evt.Date ?? DateTimeOffset.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Spaceflight events API failed: {ex.Message}");
        }

        return items;
    }

    // API response models
    private record SnapiResponse(int Count, List<SnapiArticle>? Results);
    private record SnapiArticle(
        int Id, string? Title, string? Url, string? ImageUrl,
        [property: JsonPropertyName("news_site")] string? NewsSite,
        string? Summary,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt);

    private record SnapiEventResponse(int Count, List<SnapiEvent>? Results);
    private record SnapiEvent(
        int Id, string? Title, string? Url, string? Description,
        string? Provider, DateTimeOffset? Date);
}

using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Google Custom Search JSON API (Programmable Search Engine).
/// Free tier: 100 queries/day. Paid: $5/1000 queries.
/// Requires API key + Custom Search Engine ID (CX).
/// </summary>
public class GoogleSearchService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget, CircuitBreakerService circuit)
{
    private const string ServiceName = "google_search";
    private const string Endpoint = "https://www.googleapis.com/customsearch/v1";

    public bool IsAvailable => keys.HasGoogleSearch && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    /// Search Google and return results as ContentItems.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("Google Search API not configured — skipping");
            return [];
        }

        var svcEntry = keys.GetService(ServiceName)!;
        var items = new List<ContentItem>();

        // Google CSE returns max 10 per request; need pagination for more
        var pages = Math.Min(3, (maxResults + 9) / 10);

        for (var page = 0; page < pages && items.Count < maxResults; page++)
        {
            if (!await circuit.IsServiceAvailableAsync(ServiceName))
                break;

            var check = await budget.CheckBudgetAsync(ServiceName);
            if (!check.IsAllowed)
            {
                var failureType = CircuitBreakerService.ClassifyBudgetDenial(check.DenialReason);
                await circuit.TripCircuitAsync(ServiceName, failureType, check.DenialReason);
                break;
            }

            var startIndex = page * 10 + 1;
            var url = $"{Endpoint}?key={svcEntry.ApiKey}&cx={svcEntry.SearchEngineId}" +
                      $"&q={HttpUtility.UrlEncode(query)}&start={startIndex}&num=10";

            progress?.Invoke($"Searching Google (page {page + 1}) for: {query}");

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await httpClient.SendAsync(request, cts.Token);

                await budget.RecordUsageAsync(ServiceName);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token);
                    AnsiConsole.MarkupLine($"[yellow]Google Search API {(int)response.StatusCode}: {Truncate(body, 200)}[/]");
                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                items.AddRange(ParseResults(json));
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]Google Search timed out[/]");
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Google Search error: {ex.Message}[/]");
                break;
            }
        }

        progress?.Invoke($"Google Search: {items.Count} results");
        return items.Take(maxResults).ToList();
    }

    private static List<ContentItem> ParseResults(string json)
    {
        var items = new List<ContentItem>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("items", out var itemsEl))
            return items;

        foreach (var item in itemsEl.EnumerateArray())
        {
            var title = item.GetProperty("title").GetString();
            var link = item.GetProperty("link").GetString();
            var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() : null;

            string? imageUrl = null;
            if (item.TryGetProperty("pagemap", out var pagemap))
            {
                if (pagemap.TryGetProperty("cse_thumbnail", out var thumbs) && thumbs.GetArrayLength() > 0)
                    imageUrl = thumbs[0].TryGetProperty("src", out var src) ? src.GetString() : null;
                else if (pagemap.TryGetProperty("cse_image", out var imgs) && imgs.GetArrayLength() > 0)
                    imageUrl = imgs[0].TryGetProperty("src", out var src2) ? src2.GetString() : null;
            }

            var createdAt = DateTimeOffset.UtcNow;
            if (item.TryGetProperty("pagemap", out var pm2) &&
                pm2.TryGetProperty("metatags", out var metatags) && metatags.GetArrayLength() > 0)
            {
                var meta = metatags[0];
                var dateStr =
                    meta.TryGetProperty("article:published_time", out var d1) ? d1.GetString() :
                    meta.TryGetProperty("og:updated_time", out var d2) ? d2.GetString() :
                    meta.TryGetProperty("date", out var d3) ? d3.GetString() : null;
                if (DateTimeOffset.TryParse(dateStr, out var parsed))
                    createdAt = parsed;
            }

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(link))
            {
                items.Add(new ContentItem
                {
                    Id = $"gsearch_{Hash(link)}",
                    Source = "google_search",
                    Title = title,
                    Url = link,
                    Content = snippet,
                    ImageUrl = imageUrl,
                    CreatedAt = createdAt
                });
            }
        }

        return items;
    }

    private static string Hash(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}

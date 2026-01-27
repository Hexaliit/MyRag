using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Serper.dev — fast Google SERP API.
/// Free tier: 2,500 queries total (one-time). Then $0.30/1000.
/// Supports web search and news search.
/// </summary>
public class SerperSearchService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget)
{
    private const string ServiceName = "serper";
    private const string WebEndpoint = "https://google.serper.dev/search";
    private const string NewsEndpoint = "https://google.serper.dev/news";

    public bool IsAvailable => keys.IsAvailable(ServiceName);

    /// <summary>
    /// Search via Serper.dev and return results as ContentItems.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, bool newsOnly = false,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("Serper not configured — skipping");
            return [];
        }

        var check = await budget.CheckBudgetAsync(ServiceName);
        if (!check.IsAllowed)
        {
            AnsiConsole.MarkupLine($"[yellow]Serper: {check.DenialReason}[/]");
            return [];
        }

        var svcEntry = keys.GetService(ServiceName)!;
        var endpoint = newsOnly ? NewsEndpoint : WebEndpoint;

        var payload = new Dictionary<string, object>
        {
            ["q"] = query,
            ["num"] = Math.Min(maxResults, 20)
        };

        if (newsOnly)
            payload["tbs"] = "qdr:d"; // past day

        progress?.Invoke($"Searching Serper{(newsOnly ? " News" : "")} for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var jsonPayload = JsonSerializer.Serialize(payload);
            var response = await ApiRateLimiter.ExecuteAsync(ServiceName, async token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("X-API-KEY", svcEntry.ApiKey);
                return await httpClient.SendAsync(req, token);
            }, cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                AnsiConsole.MarkupLine($"[yellow]Serper {(int)response.StatusCode}: {Truncate(body, 200)}[/]");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = newsOnly ? ParseNewsResults(json) : ParseWebResults(json);

            progress?.Invoke($"Serper: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Serper timed out[/]");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Serper error: {ex.Message}[/]");
            return [];
        }
    }

    private static List<ContentItem> ParseWebResults(string json)
    {
        var items = new List<ContentItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("organic", out var organic))
            return items;

        foreach (var r in organic.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var link = r.TryGetProperty("link", out var l) ? l.GetString() : null;
            var snippet = r.TryGetProperty("snippet", out var s) ? s.GetString() : null;
            var imageUrl = r.TryGetProperty("imageUrl", out var img) ? img.GetString() : null;

            var createdAt = DateTimeOffset.UtcNow;
            if (r.TryGetProperty("date", out var d) && DateTimeOffset.TryParse(d.GetString(), out var parsed))
                createdAt = parsed;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(link))
            {
                items.Add(new ContentItem
                {
                    Id = $"serper_{Hash(link)}",
                    Source = "serper",
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

    private static List<ContentItem> ParseNewsResults(string json)
    {
        var items = new List<ContentItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("news", out var news))
            return items;

        foreach (var r in news.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var link = r.TryGetProperty("link", out var l) ? l.GetString() : null;
            var snippet = r.TryGetProperty("snippet", out var s) ? s.GetString() : null;
            var imageUrl = r.TryGetProperty("imageUrl", out var img) ? img.GetString() : null;
            var source = r.TryGetProperty("source", out var src) ? src.GetString() : null;

            var createdAt = DateTimeOffset.UtcNow;
            if (r.TryGetProperty("date", out var d) && DateTimeOffset.TryParse(d.GetString(), out var parsed))
                createdAt = parsed;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(link))
            {
                items.Add(new ContentItem
                {
                    Id = $"serper_news_{Hash(link)}",
                    Source = "serper_news",
                    Title = title,
                    Url = link,
                    Content = snippet,
                    ImageUrl = imageUrl,
                    CreatedAt = createdAt,
                    Metadata = source != null ? new() { ["source_name"] = source } : null
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

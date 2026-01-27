using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Brave Search API — independent index, privacy-first.
/// Free tier: 2,000 queries/month (~66/day). No credit card needed for Data for AI plan.
/// Supports web search and news search.
/// </summary>
public class BraveSearchService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget, CircuitBreakerService circuit)
{
    private const string ServiceName = "brave_search";
    private const string WebEndpoint = "https://api.search.brave.com/res/v1/web/search";
    private const string NewsEndpoint = "https://api.search.brave.com/res/v1/news/search";

    public bool IsAvailable => keys.IsAvailable(ServiceName) && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    /// Search Brave and return results as ContentItems.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, bool newsOnly = false,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!keys.IsAvailable(ServiceName))
        {
            progress?.Invoke("Brave Search not configured — skipping");
            return [];
        }

        if (!await circuit.IsServiceAvailableAsync(ServiceName))
            return [];

        var check = await budget.CheckBudgetAsync(ServiceName);
        if (!check.IsAllowed)
        {
            var failureType = CircuitBreakerService.ClassifyBudgetDenial(check.DenialReason);
            await circuit.TripCircuitAsync(ServiceName, failureType, check.DenialReason);
            return [];
        }

        var svcEntry = keys.GetService(ServiceName)!;
        var endpoint = newsOnly ? NewsEndpoint : WebEndpoint;
        var url = $"{endpoint}?q={HttpUtility.UrlEncode(query)}&count={Math.Min(maxResults, 20)}";

        if (newsOnly)
            url += "&freshness=pd"; // past day

        progress?.Invoke($"Searching Brave{(newsOnly ? " News" : "")} for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var response = await ApiRateLimiter.ExecuteAsync(ServiceName, async token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Subscription-Token", svcEntry.ApiKey);
                req.Headers.Add("Accept", "application/json");
                return await httpClient.SendAsync(req, token);
            }, cts.Token);

            await budget.RecordUsageAsync(ServiceName);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                AnsiConsole.MarkupLine($"[yellow]Brave Search {(int)response.StatusCode}: {Truncate(body, 200)}[/]");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = newsOnly ? ParseNewsResults(json) : ParseWebResults(json);

            progress?.Invoke($"Brave Search: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Brave Search timed out[/]");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Brave Search error: {ex.Message}[/]");
            return [];
        }
    }

    private static List<ContentItem> ParseWebResults(string json)
    {
        var items = new List<ContentItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var results))
            return items;

        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            var description = r.TryGetProperty("description", out var d) ? d.GetString() : null;
            var thumbnail = r.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("src", out var src)
                ? src.GetString() : null;

            var createdAt = DateTimeOffset.UtcNow;
            if (r.TryGetProperty("age", out var age))
            {
                // Brave returns "2 hours ago", "3 days ago" etc. — parse as best-effort
                createdAt = ParseAge(age.GetString()) ?? createdAt;
            }
            else if (r.TryGetProperty("page_age", out var pageAge) &&
                     DateTimeOffset.TryParse(pageAge.GetString(), out var parsed))
            {
                createdAt = parsed;
            }

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                items.Add(new ContentItem
                {
                    Id = $"brave_{Hash(url)}",
                    Source = "brave_search",
                    Title = title,
                    Url = url,
                    Content = description,
                    ImageUrl = thumbnail,
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

        if (!root.TryGetProperty("results", out var results))
            return items;

        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            var description = r.TryGetProperty("description", out var d) ? d.GetString() : null;
            var thumbnail = r.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("src", out var src)
                ? src.GetString() : null;
            var source = r.TryGetProperty("meta_url", out var mu) && mu.TryGetProperty("hostname", out var h)
                ? h.GetString() : null;

            var createdAt = DateTimeOffset.UtcNow;
            if (r.TryGetProperty("age", out var age))
                createdAt = ParseAge(age.GetString()) ?? createdAt;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                items.Add(new ContentItem
                {
                    Id = $"brave_news_{Hash(url)}",
                    Source = "brave_news",
                    Title = title,
                    Url = url,
                    Content = description,
                    ImageUrl = thumbnail,
                    CreatedAt = createdAt,
                    Metadata = source != null ? new() { ["source_domain"] = source } : null
                });
            }
        }
        return items;
    }

    private static DateTimeOffset? ParseAge(string? ageStr)
    {
        if (string.IsNullOrEmpty(ageStr)) return null;
        var now = DateTimeOffset.UtcNow;
        var parts = ageStr.Split(' ');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var n)) return null;
        return parts[1] switch
        {
            _ when parts[1].StartsWith("hour") => now.AddHours(-n),
            _ when parts[1].StartsWith("minute") => now.AddMinutes(-n),
            _ when parts[1].StartsWith("day") => now.AddDays(-n),
            _ when parts[1].StartsWith("week") => now.AddDays(-n * 7),
            _ when parts[1].StartsWith("month") => now.AddDays(-n * 30),
            _ => null
        };
    }

    private static string Hash(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}

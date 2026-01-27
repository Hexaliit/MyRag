using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Tavily AI Search API — optimised for AI agents.
/// Free tier: 1,000 searches/month (~33/day).
/// Returns AI-scored, filtered, and ranked results.
/// </summary>
public class TavilySearchService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget)
{
    private const string ServiceName = "tavily";
    private const string Endpoint = "https://api.tavily.com/search";

    public bool IsAvailable => keys.IsAvailable(ServiceName);

    /// <summary>
    /// Search via Tavily and return results as ContentItems.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, bool newsOnly = false,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("Tavily not configured — skipping");
            return [];
        }

        var check = await budget.CheckBudgetAsync(ServiceName);
        if (!check.IsAllowed)
        {
            AnsiConsole.MarkupLine($"[yellow]Tavily: {check.DenialReason}[/]");
            return [];
        }

        var svcEntry = keys.GetService(ServiceName)!;

        var payload = new Dictionary<string, object>
        {
            ["query"] = query,
            ["max_results"] = Math.Min(maxResults, 20),
            ["search_depth"] = "basic",
            ["include_answer"] = false,
            ["include_raw_content"] = false,
            ["include_images"] = false
        };

        if (newsOnly)
        {
            payload["topic"] = "news";
            payload["days"] = 1;
        }

        progress?.Invoke($"Searching Tavily{(newsOnly ? " News" : "")} for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15)); // Tavily can be slower

            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bearer {svcEntry.ApiKey}");

            var response = await httpClient.SendAsync(request, cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                AnsiConsole.MarkupLine($"[yellow]Tavily {(int)response.StatusCode}: {Truncate(body, 200)}[/]");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = ParseResults(json);

            progress?.Invoke($"Tavily: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Tavily timed out[/]");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Tavily error: {ex.Message}[/]");
            return [];
        }
    }

    private static List<ContentItem> ParseResults(string json)
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
            var content = r.TryGetProperty("content", out var c) ? c.GetString() : null;
            var score = r.TryGetProperty("score", out var s) ? s.GetDouble() : 0;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                items.Add(new ContentItem
                {
                    Id = $"tavily_{Hash(url)}",
                    Source = "tavily",
                    Title = title,
                    Url = url,
                    Content = content,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Metadata = new() { ["relevance_score"] = score.ToString("F3") }
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

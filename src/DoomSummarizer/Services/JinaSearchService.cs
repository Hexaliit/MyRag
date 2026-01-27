using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Jina AI Search API — embeddings-powered semantic search.
/// Free tier: no credit card needed, rate-limited (slower without API key).
/// With API key: higher priority, faster results.
/// Returns clean content extracted from search results.
/// </summary>
public class JinaSearchService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget, CircuitBreakerService circuit)
{
    private const string ServiceName = "jina";
    private const string Endpoint = "https://s.jina.ai/";

    public bool IsAvailable => keys.IsAvailable(ServiceName) && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    /// Search via Jina AI and return results as ContentItems.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 5,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("Jina not configured — skipping");
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

        progress?.Invoke($"Searching Jina AI for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15)); // Jina can be slow

            var jsonPayload = JsonSerializer.Serialize(new { q = query });
            var response = await ApiRateLimiter.ExecuteAsync(ServiceName, async token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Authorization", $"Bearer {svcEntry.ApiKey}");
                req.Headers.Add("Accept", "application/json");
                req.Headers.Add("X-No-Cache", "true");
                return await httpClient.SendAsync(req, token);
            }, cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                AnsiConsole.MarkupLine($"[yellow]Jina {(int)response.StatusCode}: {Truncate(body, 200)}[/]");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = ParseResults(json);

            progress?.Invoke($"Jina: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Jina timed out[/]");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Jina error: {ex.Message}[/]");
            return [];
        }
    }

    private static List<ContentItem> ParseResults(string json)
    {
        var items = new List<ContentItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Jina returns: {"code":200,"status":20000,"data":[...]}
        if (!root.TryGetProperty("data", out var data))
            return items;

        foreach (var r in data.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            var content = r.TryGetProperty("content", out var c) ? c.GetString() : null;
            var description = r.TryGetProperty("description", out var d) ? d.GetString() : null;

            // Jina returns full page content — truncate for our use
            var bestContent = !string.IsNullOrEmpty(description) ? description :
                !string.IsNullOrEmpty(content) ? Truncate(content, 1000) : null;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                items.Add(new ContentItem
                {
                    Id = $"jina_{Hash(url)}",
                    Source = "jina",
                    Title = title,
                    Url = url,
                    Content = bestContent,
                    CreatedAt = DateTimeOffset.UtcNow
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

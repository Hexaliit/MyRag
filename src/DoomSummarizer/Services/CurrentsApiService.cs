using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Currents API — aggregated latest news from 17,000+ sources in 18 languages.
/// Free tier: 600 requests/day. Response includes title, description, url, image, category.
/// Endpoint: https://api.currentsapi.services/v1/latest-news or /search
/// Docs: https://currentsapi.services/en/docs/
/// </summary>
public class CurrentsApiService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget, CircuitBreakerService circuit)
{
    private const string ServiceName = "currents";
    private const string SearchEndpoint = "https://api.currentsapi.services/v1/search";
    private const string LatestEndpoint = "https://api.currentsapi.services/v1/latest-news";

    public bool IsAvailable => keys.IsAvailable(ServiceName) && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    /// Search Currents API for news articles matching a query.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, string? category = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("Currents API not configured — skipping");
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

        // Build URL: search endpoint for queries, latest for no query
        var endpoint = string.IsNullOrWhiteSpace(query) ? LatestEndpoint : SearchEndpoint;
        var url = $"{endpoint}?apiKey={svcEntry.ApiKey}&language=en";

        if (!string.IsNullOrWhiteSpace(query))
            url += $"&keywords={HttpUtility.UrlEncode(query)}";

        if (!string.IsNullOrEmpty(category))
            url += $"&category={HttpUtility.UrlEncode(category)}";

        progress?.Invoke($"Searching Currents for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await ApiRateLimiter.ExecuteAsync(ServiceName, async token =>
                await httpClient.GetAsync(url, token), cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                AnsiConsole.MarkupLine($"[yellow]Currents {(int)response.StatusCode}: {Truncate(body, 200)}[/]");
                await circuit.ReportFailureAsync(ServiceName, CircuitFailureType.ServerError,
                    $"HTTP {(int)response.StatusCode}");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = ParseResults(json);

            await circuit.ReportSuccessAsync(ServiceName);
            progress?.Invoke($"Currents: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Currents API timed out[/]");
            await circuit.ReportFailureAsync(ServiceName, CircuitFailureType.ServerError, "Timeout");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Currents API error: {Markup.Escape(ex.Message)}[/]");
            await circuit.ReportFailureAsync(ServiceName, CircuitFailureType.ServerError, ex.Message);
            return [];
        }
    }

    private static List<ContentItem> ParseResults(string json)
    {
        var items = new List<ContentItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() != "ok")
            return items;

        if (!root.TryGetProperty("news", out var news))
            return items;

        foreach (var article in news.EnumerateArray())
        {
            var title = article.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = article.TryGetProperty("url", out var u) ? u.GetString() : null;
            var description = article.TryGetProperty("description", out var d) ? d.GetString() : null;
            var image = article.TryGetProperty("image", out var img) && img.GetString() != "None"
                ? img.GetString() : null;
            var author = article.TryGetProperty("author", out var a) ? a.GetString() : null;

            var createdAt = DateTimeOffset.UtcNow;
            if (article.TryGetProperty("published", out var pub) &&
                DateTimeOffset.TryParse(pub.GetString(), out var parsed))
                createdAt = parsed;

            // Extract category as keyword
            var keywords = new List<string>();
            if (article.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cat.EnumerateArray())
                {
                    var catStr = c.GetString();
                    if (!string.IsNullOrEmpty(catStr) && catStr != "Uncategorized")
                        keywords.Add(catStr);
                }
            }

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                var metadata = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(author)) metadata["author"] = author;
                if (keywords.Count > 0) metadata["keywords"] = string.Join(", ", keywords);

                items.Add(new ContentItem
                {
                    Id = $"currents_{Hash(url)}",
                    Source = "currents",
                    Title = title,
                    Url = url,
                    Content = description,
                    ImageUrl = image,
                    CreatedAt = createdAt,
                    Metadata = metadata.Count > 0 ? metadata : null
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

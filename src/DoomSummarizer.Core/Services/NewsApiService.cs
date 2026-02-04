using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     NewsAPI.org — purpose-built news search API.
///     Free tier: 100 requests/day, max 100 articles/request, 1-month historical.
///     Returns news articles from 150,000+ sources worldwide.
///     Note: Free tier is dev-only (no production use, returns cached/delayed results).
/// </summary>
public class NewsApiService(
    HttpClient httpClient,
    ApiKeyService keys,
    ApiBudgetService budget,
    CircuitBreakerService circuit)
{
    private const string ServiceName = "newsapi";
    private const string EverythingEndpoint = "https://newsapi.org/v2/everything";
    private const string TopHeadlinesEndpoint = "https://newsapi.org/v2/top-headlines";

    public bool IsAvailable => keys.IsAvailable(ServiceName) && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    ///     Search news via NewsAPI.org.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, bool headlinesOnly = false,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("NewsAPI not configured — skipping");
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

        string url;
        if (headlinesOnly)
            url = $"{TopHeadlinesEndpoint}?q={HttpUtility.UrlEncode(query)}" +
                  $"&pageSize={Math.Min(maxResults, 100)}&language=en";
        else
            url = $"{EverythingEndpoint}?q={HttpUtility.UrlEncode(query)}" +
                  $"&pageSize={Math.Min(maxResults, 100)}&sortBy=publishedAt&language=en";

        progress?.Invoke($"Searching NewsAPI{(headlinesOnly ? " Headlines" : "")} for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await ApiRateLimiter.ExecuteAsync(ServiceName, async token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Api-Key", svcEntry.ApiKey);
                return await httpClient.SendAsync(req, token);
            }, cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                Debug.WriteLine($"NewsAPI {(int)response.StatusCode}: {Truncate(body, 200)}");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = ParseResults(json);

            progress?.Invoke($"NewsAPI: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("NewsAPI timed out");
            return [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NewsAPI error: {ex.Message}");
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

        if (!root.TryGetProperty("articles", out var articles))
            return items;

        foreach (var a in articles.EnumerateArray())
        {
            var title = a.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = a.TryGetProperty("url", out var u) ? u.GetString() : null;
            var description = a.TryGetProperty("description", out var d) ? d.GetString() : null;
            var content = a.TryGetProperty("content", out var c) ? c.GetString() : null;
            var imageUrl = a.TryGetProperty("urlToImage", out var img) ? img.GetString() : null;
            var author = a.TryGetProperty("author", out var au) ? au.GetString() : null;

            var sourceName = a.TryGetProperty("source", out var src) && src.TryGetProperty("name", out var sn)
                ? sn.GetString()
                : null;

            var createdAt = DateTimeOffset.UtcNow;
            if (a.TryGetProperty("publishedAt", out var pub) &&
                DateTimeOffset.TryParse(pub.GetString(), out var parsed))
                createdAt = parsed;

            // NewsAPI free tier truncates content to 200 chars with "[+N chars]"
            // Prefer description over truncated content
            var bestContent = !string.IsNullOrEmpty(description) ? description :
                !string.IsNullOrEmpty(content) ? content : null;

            // Skip "[Removed]" articles (sometimes returned by NewsAPI)
            if (title == "[Removed]" || url == null) continue;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                var metadata = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(sourceName)) metadata["source_name"] = sourceName;
                if (!string.IsNullOrEmpty(author)) metadata["author"] = author;

                items.Add(new ContentItem
                {
                    Id = $"newsapi_{Hash(url)}",
                    Source = "newsapi",
                    Title = title,
                    Url = url,
                    Content = bestContent,
                    ImageUrl = imageUrl,
                    CreatedAt = createdAt,
                    Metadata = metadata.Count > 0 ? metadata : null
                });
            }
        }

        return items;
    }

    private static string Hash(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }

    private static string Truncate(string s, int max)
    {
        return s.Length > max ? s[..max] + "..." : s;
    }
}
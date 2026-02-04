using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     NewsData.io — real-time and historical news API.
///     Free tier: 200 credits/day (10 articles/credit = 2,000 articles/day).
///     Rate limit: 30 credits per 15 minutes. 12-hour delay on free tier.
///     Covers 89 languages, 65+ countries, 53,000+ sources.
/// </summary>
public class NewsDataService(
    HttpClient httpClient,
    ApiKeyService keys,
    ApiBudgetService budget,
    CircuitBreakerService circuit)
{
    private const string ServiceName = "newsdata";
    private const string Endpoint = "https://newsdata.io/api/1/latest";

    public bool IsAvailable => keys.IsAvailable(ServiceName) && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    ///     Search news via NewsData.io.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 10, string? category = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("NewsData not configured — skipping");
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

        // Free tier: max 10 articles per request, 100 char query limit
        var truncatedQuery = query.Length > 100 ? query[..100] : query;
        var url = $"{Endpoint}?apikey={svcEntry.ApiKey}" +
                  $"&q={HttpUtility.UrlEncode(truncatedQuery)}&language=en";

        if (!string.IsNullOrEmpty(category))
            url += $"&category={category}";

        progress?.Invoke($"Searching NewsData for: {query}");

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
                Debug.WriteLine($"NewsData {(int)response.StatusCode}: {Truncate(body, 200)}");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = ParseResults(json);

            // If we need more and there's a next page, fetch more (up to maxResults)
            if (items.Count < maxResults)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("nextPage", out var nextPage) &&
                    nextPage.GetString() is { Length: > 0 } cursor)
                    if (await circuit.IsServiceAvailableAsync(ServiceName))
                    {
                        var check2 = await budget.CheckBudgetAsync(ServiceName);
                        if (check2.IsAllowed)
                        {
                            var url2 = $"{Endpoint}?apikey={svcEntry.ApiKey}" +
                                       $"&q={HttpUtility.UrlEncode(truncatedQuery)}&language=en&page={cursor}";
                            var response2 = await httpClient.GetAsync(url2, cts.Token);
                            await budget.RecordUsageAsync(ServiceName);
                            if (response2.IsSuccessStatusCode)
                            {
                                var json2 = await response2.Content.ReadAsStringAsync(cts.Token);
                                items.AddRange(ParseResults(json2));
                            }
                        }
                        else
                        {
                            var ft = CircuitBreakerService.ClassifyBudgetDenial(check2.DenialReason);
                            await circuit.TripCircuitAsync(ServiceName, ft, check2.DenialReason);
                        }
                    }
            }

            progress?.Invoke($"NewsData: {items.Count} results");
            return items.Take(maxResults).ToList();
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("NewsData timed out");
            return [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NewsData error: {ex.Message}");
            return [];
        }
    }

    private static List<ContentItem> ParseResults(string json)
    {
        var items = new List<ContentItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() != "success")
            return items;

        if (!root.TryGetProperty("results", out var results))
            return items;

        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var link = r.TryGetProperty("link", out var l) ? l.GetString() : null;
            var description = r.TryGetProperty("description", out var d) ? d.GetString() : null;
            var content = r.TryGetProperty("content", out var c) ? c.GetString() : null;
            var imageUrl = r.TryGetProperty("image_url", out var img) ? img.GetString() : null;
            var sourceId = r.TryGetProperty("source_id", out var src) ? src.GetString() : null;
            var creator = r.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Array
                ? string.Join(", ", cr.EnumerateArray().Select(x => x.GetString()))
                : null;
            var isDuplicate = r.TryGetProperty("duplicate", out var dup) && dup.GetBoolean();

            // Skip duplicates
            if (isDuplicate) continue;

            var createdAt = DateTimeOffset.UtcNow;
            if (r.TryGetProperty("pubDate", out var pub) &&
                DateTimeOffset.TryParse(pub.GetString(), out var parsed))
                createdAt = parsed;

            // Use full content if available, otherwise description
            var bestContent = !string.IsNullOrEmpty(content) ? content :
                !string.IsNullOrEmpty(description) ? description : null;

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(link))
            {
                var metadata = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(sourceId)) metadata["source_id"] = sourceId;
                if (!string.IsNullOrEmpty(creator)) metadata["author"] = creator;

                // Extract keywords if present
                if (r.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
                {
                    var keywords = string.Join(", ", kw.EnumerateArray()
                        .Where(k => k.ValueKind == JsonValueKind.String)
                        .Select(k => k.GetString()));
                    if (!string.IsNullOrEmpty(keywords))
                        metadata["keywords"] = keywords;
                }

                items.Add(new ContentItem
                {
                    Id = $"newsdata_{Hash(link)}",
                    Source = "newsdata",
                    Title = title,
                    Url = link,
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
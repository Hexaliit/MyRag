using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Google Places API (Text Search).
/// Useful for entity disambiguation (company identification), location-based queries,
/// and enriching news stories with business/location context.
/// Cost: ~$17/1000 requests (Text Search), ~$32/1000 (Details).
/// </summary>
public class GooglePlacesService(HttpClient httpClient, ApiKeyService keys, ApiBudgetService budget, CircuitBreakerService circuit)
{
    private const string ServiceName = "google_places";
    private const string TextSearchEndpoint = "https://maps.googleapis.com/maps/api/place/textsearch/json";
    private const string DetailsEndpoint = "https://maps.googleapis.com/maps/api/place/details/json";

    public bool IsAvailable => keys.HasGooglePlaces && !circuit.IsCircuitOpen(ServiceName);

    /// <summary>
    /// Search for places/businesses matching a query. Returns ContentItems for pipeline integration.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(
        string query, int maxResults = 5, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            progress?.Invoke("Google Places API not configured — skipping");
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

        var apiKey = keys.GetService(ServiceName)!.ApiKey;
        progress?.Invoke($"Searching Google Places for: {query}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var url = $"{TextSearchEndpoint}?query={HttpUtility.UrlEncode(query)}&key={apiKey}";

            var response = await httpClient.GetAsync(url, cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var items = ParseTextSearchResults(json, maxResults);

            progress?.Invoke($"Google Places: {items.Count} results");
            return items;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Google Places error: {ex.Message}[/]");
            return [];
        }
    }

    /// <summary>
    /// Get detailed place info by place_id. Useful for entity disambiguation.
    /// </summary>
    public async Task<PlaceDetails?> GetDetailsAsync(string placeId, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;

        if (!await circuit.IsServiceAvailableAsync(ServiceName))
            return null;

        var check = await budget.CheckBudgetAsync(ServiceName);
        if (!check.IsAllowed)
        {
            var failureType = CircuitBreakerService.ClassifyBudgetDenial(check.DenialReason);
            await circuit.TripCircuitAsync(ServiceName, failureType, check.DenialReason);
            return null;
        }

        var apiKey = keys.GetService(ServiceName)!.ApiKey;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var fields = "name,formatted_address,types,website,url,rating,user_ratings_total,business_status,opening_hours";
            var url = $"{DetailsEndpoint}?place_id={placeId}&fields={fields}&key={apiKey}";

            var response = await httpClient.GetAsync(url, cts.Token);
            await budget.RecordUsageAsync(ServiceName);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return ParseDetailsResult(json);
        }
        catch
        {
            return null;
        }
    }

    private static List<ContentItem> ParseTextSearchResults(string json, int maxResults)
    {
        var items = new List<ContentItem>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var results))
            return items;

        foreach (var result in results.EnumerateArray().Take(maxResults))
        {
            var name = result.TryGetProperty("name", out var n) ? n.GetString() : null;
            var address = result.TryGetProperty("formatted_address", out var a) ? a.GetString() : null;
            var placeId = result.TryGetProperty("place_id", out var p) ? p.GetString() : null;
            var rating = result.TryGetProperty("rating", out var r) ? r.GetDouble() : 0;
            var totalRatings = result.TryGetProperty("user_ratings_total", out var tr) ? tr.GetInt32() : 0;

            var types = new List<string>();
            if (result.TryGetProperty("types", out var typesEl))
            {
                foreach (var t in typesEl.EnumerateArray())
                    if (t.GetString() is { } typeStr)
                        types.Add(typeStr.Replace('_', ' '));
            }

            var description = address ?? "";
            if (rating > 0)
                description += $" | Rating: {rating}/5 ({totalRatings} reviews)";
            if (types.Count > 0)
                description += $" | Type: {string.Join(", ", types.Take(3))}";

            if (!string.IsNullOrEmpty(name))
            {
                items.Add(new ContentItem
                {
                    Id = $"places_{placeId ?? name.GetHashCode().ToString()}",
                    Source = "google_places",
                    Title = name,
                    Url = placeId != null ? $"https://www.google.com/maps/place/?q=place_id:{placeId}" : null,
                    Content = description,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["place_id"] = placeId ?? "",
                        ["address"] = address ?? "",
                        ["rating"] = rating.ToString("F1"),
                        ["types"] = string.Join(",", types)
                    }
                });
            }
        }

        return items;
    }

    private static PlaceDetails? ParseDetailsResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var result))
            return null;

        return new PlaceDetails
        {
            Name = result.TryGetProperty("name", out var n) ? n.GetString() : null,
            Address = result.TryGetProperty("formatted_address", out var a) ? a.GetString() : null,
            Website = result.TryGetProperty("website", out var w) ? w.GetString() : null,
            MapsUrl = result.TryGetProperty("url", out var u) ? u.GetString() : null,
            Rating = result.TryGetProperty("rating", out var r) ? r.GetDouble() : 0,
            TotalRatings = result.TryGetProperty("user_ratings_total", out var tr) ? tr.GetInt32() : 0,
            BusinessStatus = result.TryGetProperty("business_status", out var bs) ? bs.GetString() : null,
            Types = result.TryGetProperty("types", out var types)
                ? types.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : []
        };
    }
}

public record PlaceDetails
{
    public string? Name { get; init; }
    public string? Address { get; init; }
    public string? Website { get; init; }
    public string? MapsUrl { get; init; }
    public double Rating { get; init; }
    public int TotalRatings { get; init; }
    public string? BusinessStatus { get; init; }
    public List<string> Types { get; init; } = [];
}

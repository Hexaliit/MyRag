using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Fetches crime data from the UK Police Data API — free, no auth, no rate limits.
///     Covers street-level crime, police force info, and neighbourhood data.
///     https://data.police.uk/docs/
/// </summary>
public class UkPoliceFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://data.police.uk/api";
    private const string UserAgent = "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)";

    /// <summary>
    ///     Available sub-parameter sections.
    ///     Usage: -s ukpolice, -s ukpolice:forces, -s ukpolice:crime:52.629729,-1.131592
    /// </summary>
    public static readonly IReadOnlyList<string> AvailableSections =
        ["forces", "crime", "outcomes", "neighbourhoods"];

    /// <summary>
    ///     Fetch police data based on section and optional parameters.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? section = null,
        IReadOnlyList<string>? subParams = null)
    {
        return section switch
        {
            "crime" => await FetchStreetCrimeAsync(limit, subParams),
            "forces" => await FetchForcesAsync(limit),
            "outcomes" => await FetchOutcomesAsync(limit, subParams),
            _ => await FetchCrimeSummaryAsync(limit)
        };
    }

    /// <summary>
    ///     Fetch a summary of recent crime categories and totals.
    ///     Used as the default when no specific section is requested.
    /// </summary>
    public async Task<List<ContentItem>> FetchCrimeSummaryAsync(int limit = 20)
    {
        var items = new List<ContentItem>();

        try
        {
            // Get available date ranges
            var dates = await FetchJsonAsync<List<PoliceAvailability>>(
                $"{BaseUrl}/crimes-street-dates");

            if (dates == null || dates.Count == 0) return items;

            var latestDate = dates[0].Date; // Most recent month

            // Fetch crime categories
            var categories = await FetchJsonAsync<List<CrimeCategory>>(
                $"{BaseUrl}/crime-categories?date={latestDate}");

            if (categories == null) return items;

            foreach (var cat in categories.Take(limit))
            {
                if (string.IsNullOrEmpty(cat.Name)) continue;

                items.Add(new ContentItem
                {
                    Id = $"ukpolice_cat_{GenerateId(cat.Url ?? cat.Name)}",
                    Source = "ukpolice",
                    Title = $"[Crime Category] {cat.Name}",
                    Url = "https://data.police.uk/docs/method/crime-categories/",
                    Content = $"Crime category: {cat.Name}. Data available for {latestDate}. " +
                              "Use -s ukpolice:crime:LAT,LNG to search street-level crime by location.",
                    Author = "UK Police Data",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = ["crime", "uk"],
                    Metadata = new Dictionary<string, string>
                    {
                        ["category"] = cat.Url ?? cat.Name,
                        ["date"] = latestDate ?? ""
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: UK Police crime summary failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Fetch street-level crime data for a location.
    ///     Expects subParams[0] = "lat,lng" (e.g. "52.629729,-1.131592" for Leicester).
    /// </summary>
    public async Task<List<ContentItem>> FetchStreetCrimeAsync(int limit = 20, IReadOnlyList<string>? subParams = null)
    {
        var items = new List<ContentItem>();

        try
        {
            // Default to central London if no coordinates provided
            var coords = "51.5074,-0.1278";
            if (subParams is { Count: > 0 } && !string.IsNullOrEmpty(subParams[0]))
                coords = subParams[0];

            var parts = coords.Split(',');
            if (parts.Length != 2) return items;

            var lat = parts[0].Trim();
            var lng = parts[1].Trim();

            var url = $"{BaseUrl}/crimes-street/all-crime?lat={lat}&lng={lng}";
            var crimes = await FetchJsonAsync<List<StreetCrime>>(url);

            if (crimes == null) return items;

            // Group by category for a useful summary
            var grouped = crimes
                .GroupBy(c => c.Category)
                .OrderByDescending(g => g.Count())
                .Take(limit);

            foreach (var group in grouped)
            {
                var category = FormatCategory(group.Key ?? "other");
                var count = group.Count();
                var locations = group
                    .Where(c => c.Location?.Street?.Name != null)
                    .Select(c => c.Location!.Street!.Name!)
                    .Distinct()
                    .Take(5);

                var locationText = locations.Any()
                    ? $"Locations include: {string.Join(", ", locations)}."
                    : "";

                var sample = group.First();

                items.Add(new ContentItem
                {
                    Id = $"ukpolice_crime_{GenerateId(category + lat + lng)}",
                    Source = "ukpolice",
                    Title = $"{count}x {category} near {lat},{lng}",
                    Url = "https://data.police.uk/",
                    Content = $"{count} incidents of {category} reported near coordinates ({lat}, {lng}). " +
                              $"Month: {sample.Month ?? "recent"}. " +
                              locationText,
                    Author = "UK Police Data",
                    Score = count,
                    CreatedAt = TryParseMonth(sample.Month),
                    Tags = ["crime", "uk", category.ToLowerInvariant()],
                    Metadata = new Dictionary<string, string>
                    {
                        ["category"] = group.Key ?? "other",
                        ["count"] = count.ToString(),
                        ["lat"] = lat,
                        ["lng"] = lng
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: UK Police street crime fetch failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Fetch all police forces in England, Wales, and Northern Ireland.
    /// </summary>
    public async Task<List<ContentItem>> FetchForcesAsync(int limit = 20)
    {
        var items = new List<ContentItem>();

        try
        {
            var forces = await FetchJsonAsync<List<PoliceForce>>($"{BaseUrl}/forces");
            if (forces == null) return items;

            foreach (var force in forces.Take(limit))
            {
                if (string.IsNullOrEmpty(force.Name)) continue;

                items.Add(new ContentItem
                {
                    Id = $"ukpolice_force_{force.Id ?? GenerateId(force.Name)}",
                    Source = "ukpolice",
                    Title = force.Name,
                    Url = $"https://data.police.uk/docs/method/force/{force.Id}/",
                    Content = $"Police force: {force.Name}. " +
                              $"Use -s ukpolice:neighbourhoods:{force.Id} to see neighbourhood teams.",
                    Author = "UK Police Data",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = ["police", "uk"],
                    Metadata = new Dictionary<string, string>
                    {
                        ["force_id"] = force.Id ?? ""
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: UK Police forces fetch failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Fetch crime outcome statistics for a police force area.
    /// </summary>
    public async Task<List<ContentItem>> FetchOutcomesAsync(int limit = 20, IReadOnlyList<string>? subParams = null)
    {
        var items = new List<ContentItem>();

        try
        {
            // Default to central London
            var coords = "51.5074,-0.1278";
            if (subParams is { Count: > 0 } && !string.IsNullOrEmpty(subParams[0]))
                coords = subParams[0];

            var parts = coords.Split(',');
            if (parts.Length != 2) return items;

            var url = $"{BaseUrl}/outcomes-at-location?lat={parts[0].Trim()}&lng={parts[1].Trim()}";
            var outcomes = await FetchJsonAsync<List<CrimeOutcome>>(url);

            if (outcomes == null) return items;

            var grouped = outcomes
                .Where(o => o.Category?.Name != null)
                .GroupBy(o => o.Category!.Name)
                .OrderByDescending(g => g.Count())
                .Take(limit);

            foreach (var group in grouped)
            {
                var count = group.Count();
                items.Add(new ContentItem
                {
                    Id = $"ukpolice_outcome_{GenerateId(group.Key + coords)}",
                    Source = "ukpolice",
                    Title = $"{count}x {group.Key}",
                    Url = "https://data.police.uk/",
                    Content = $"{count} cases with outcome: {group.Key}.",
                    Author = "UK Police Data",
                    Score = count,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = ["crime", "outcome", "uk"]
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: UK Police outcomes fetch failed: {ex.Message}");
        }

        return items;
    }

    private async Task<T?> FetchJsonAsync<T>(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        request.Headers.Add("Accept", "application/json");

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static string FormatCategory(string category)
    {
        return category.Replace('-', ' ').Replace("anti social", "anti-social");
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }

    private static DateTimeOffset TryParseMonth(string? month)
    {
        if (string.IsNullOrEmpty(month)) return DateTimeOffset.UtcNow;
        // Format: "2024-01"
        return DateTimeOffset.TryParse(month + "-01", out var r) ? r : DateTimeOffset.UtcNow;
    }

    // ── Police Data API response models ──────────────────────────────

    private record PoliceAvailability(string? Date);

    private record CrimeCategory(string? Url, string? Name);

    private record PoliceForce(string? Id, string? Name);

    private record StreetCrime(
        string? Category,
        [property: JsonPropertyName("location_type")]
        string? LocationType,
        CrimeLocation? Location,
        string? Context,
        [property: JsonPropertyName("outcome_status")]
        OutcomeStatus? OutcomeStatus,
        [property: JsonPropertyName("persistent_id")]
        string? PersistentId,
        string? Month);

    private record CrimeLocation(
        string? Latitude,
        string? Longitude,
        CrimeStreet? Street);

    private record CrimeStreet(long? Id, string? Name);

    private record OutcomeStatus(string? Category, string? Date);

    private record CrimeOutcome(
        OutcomeCategory? Category,
        string? Date,
        [property: JsonPropertyName("person_id")]
        string? PersonId);

    private record OutcomeCategory(string? Name);
}
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Fetches real-time flood monitoring data from the Environment Agency API — free, no auth.
///     Covers flood warnings, river levels, and rainfall measurements across England.
///     https://environment.data.gov.uk/flood-monitoring/doc/reference
/// </summary>
public class UkEnvironmentFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://environment.data.gov.uk/flood-monitoring";
    private const string UserAgent = "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)";

    /// <summary>
    ///     Available sections for sub-parameter routing.
    ///     Usage: -s ukflood, -s ukflood:warnings, -s ukflood:levels, -s ukflood:rainfall
    /// </summary>
    public static readonly IReadOnlyList<string> AvailableSections =
        ["warnings", "levels", "rainfall", "stations"];

    /// <summary>
    ///     Fetch flood monitoring data based on section.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? section = null, string? query = null)
    {
        return section switch
        {
            "warnings" => await FetchFloodWarningsAsync(limit),
            "levels" => await FetchHighRiverLevelsAsync(limit, query),
            "rainfall" => await FetchRainfallAsync(limit, query),
            "stations" => await FetchStationsAsync(limit, query),
            _ => await FetchFloodWarningsAsync(limit) // Default to warnings
        };
    }

    /// <summary>
    ///     Fetch current flood warnings and alerts across England.
    ///     These are the most operationally relevant items.
    /// </summary>
    public async Task<List<ContentItem>> FetchFloodWarningsAsync(int limit = 20)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/id/floods?_limit={Math.Min(limit, 50)}";
            var result = await FetchJsonAsync<FloodResponse>(url);

            if (result?.Items == null) return items;

            foreach (var flood in result.Items.Take(limit))
            {
                if (string.IsNullOrEmpty(flood.Description)) continue;

                var severity = flood.SeverityLevel ?? 4;
                var severityText = severity switch
                {
                    1 => "Severe Flood Warning",
                    2 => "Flood Warning",
                    3 => "Flood Alert",
                    _ => "Warning Removed"
                };

                var area = flood.FloodArea;
                var regionName = flood.EaRegionName ?? area?.County ?? "England";
                var riverSea = area?.RiverOrSea;
                var isTidal = flood.IsTidal == true;

                var title = $"[{severityText}] {flood.Description}";
                if (title.Length > 200) title = title[..197] + "...";

                var content = flood.Message ?? flood.Description;
                if (!string.IsNullOrEmpty(content) && content.Length > 2000)
                    content = content[..2000];

                items.Add(new ContentItem
                {
                    Id = $"ukflood_{GenerateId(flood.FloodAreaId ?? flood.Description)}",
                    Source = "ukflood",
                    Title = title,
                    Url = "https://check-for-flooding.service.gov.uk/",
                    Content = content ?? "",
                    Author = "Environment Agency",
                    Score = (5 - severity) * 100, // Higher severity = higher score
                    CreatedAt = TryParseDate(flood.TimeRaised ?? flood.TimeMessageChanged),
                    Tags = BuildFloodTags(severity, isTidal, regionName),
                    Metadata = new Dictionary<string, string>
                    {
                        ["severity"] = severity.ToString(),
                        ["severity_text"] = severityText,
                        ["region"] = regionName,
                        ["river_or_sea"] = riverSea ?? "",
                        ["is_tidal"] = isTidal.ToString()
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Environment Agency flood warnings failed: {ex.Message}");
        }

        return items
            .OrderByDescending(i => i.Score)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();
    }

    /// <summary>
    ///     Fetch monitoring stations with high river levels (above typical range).
    /// </summary>
    public async Task<List<ContentItem>> FetchHighRiverLevelsAsync(int limit = 20, string? search = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/id/stations?parameter=level&_limit={Math.Min(limit * 2, 100)}";
            if (!string.IsNullOrEmpty(search))
                url += $"&search={Uri.EscapeDataString(search)}";

            var result = await FetchJsonAsync<StationResponse>(url);
            if (result?.Items == null) return items;

            foreach (var station in result.Items.Take(limit))
            {
                if (string.IsNullOrEmpty(station.Label)) continue;

                var river = station.RiverName ?? "Unknown river";
                var town = station.Town ?? station.Label;

                items.Add(new ContentItem
                {
                    Id = $"ukflood_stn_{GenerateId(station.StationReference ?? station.Label)}",
                    Source = "ukflood",
                    Title = $"[Level] {station.Label} on {river}",
                    Url = $"https://check-for-flooding.service.gov.uk/station/{station.StationReference}",
                    Content = $"Monitoring station: {station.Label}. " +
                              $"River: {river}. Town: {town}. " +
                              (station.TypicalRangeHigh != null
                                  ? $"Typical range: {station.TypicalRangeLow ?? 0:F2}m - {station.TypicalRangeHigh:F2}m. "
                                  : "") +
                              $"Catchment: {station.CatchmentName ?? "unknown"}.",
                    Author = "Environment Agency",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = ["river-level", "monitoring", "uk"],
                    Metadata = new Dictionary<string, string>
                    {
                        ["station_ref"] = station.StationReference ?? "",
                        ["river"] = river,
                        ["town"] = town
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Environment Agency river levels failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Fetch rainfall monitoring stations.
    /// </summary>
    public async Task<List<ContentItem>> FetchRainfallAsync(int limit = 20, string? search = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/id/stations?parameter=rainfall&_limit={Math.Min(limit, 50)}";
            if (!string.IsNullOrEmpty(search))
                url += $"&search={Uri.EscapeDataString(search)}";

            var result = await FetchJsonAsync<StationResponse>(url);
            if (result?.Items == null) return items;

            foreach (var station in result.Items.Take(limit))
            {
                if (string.IsNullOrEmpty(station.Label)) continue;

                items.Add(new ContentItem
                {
                    Id = $"ukflood_rain_{GenerateId(station.StationReference ?? station.Label)}",
                    Source = "ukflood",
                    Title = $"[Rainfall] {station.Label}",
                    Url = $"https://check-for-flooding.service.gov.uk/station/{station.StationReference}",
                    Content = $"Rainfall monitoring station: {station.Label}. " +
                              $"Town: {station.Town ?? "unknown"}. " +
                              $"Catchment: {station.CatchmentName ?? "unknown"}.",
                    Author = "Environment Agency",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = ["rainfall", "monitoring", "uk"]
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Environment Agency rainfall failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    ///     Fetch monitoring stations by search term (river name, town, etc.).
    /// </summary>
    public async Task<List<ContentItem>> FetchStationsAsync(int limit = 20, string? search = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/id/stations?_limit={Math.Min(limit, 50)}";
            if (!string.IsNullOrEmpty(search))
                url += $"&search={Uri.EscapeDataString(search)}";

            var result = await FetchJsonAsync<StationResponse>(url);
            if (result?.Items == null) return items;

            foreach (var station in result.Items.Take(limit))
            {
                if (string.IsNullOrEmpty(station.Label)) continue;

                items.Add(new ContentItem
                {
                    Id = $"ukflood_stn_{GenerateId(station.StationReference ?? station.Label)}",
                    Source = "ukflood",
                    Title = $"[Station] {station.Label}",
                    Url = $"https://check-for-flooding.service.gov.uk/station/{station.StationReference}",
                    Content = $"Station: {station.Label}. " +
                              $"River: {station.RiverName ?? "N/A"}. " +
                              $"Town: {station.Town ?? "N/A"}. " +
                              $"Catchment: {station.CatchmentName ?? "N/A"}.",
                    Author = "Environment Agency",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = ["station", "monitoring", "uk"]
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Environment Agency stations failed: {ex.Message}");
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

    private static List<string> BuildFloodTags(int severity, bool isTidal, string region)
    {
        var tags = new List<string> { "flood", "uk", region.ToLowerInvariant() };
        if (severity <= 2) tags.Add("warning");
        if (severity == 1) tags.Add("severe");
        if (isTidal) tags.Add("tidal");
        return tags;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }

    private static DateTimeOffset TryParseDate(string? dateStr)
    {
        return string.IsNullOrEmpty(dateStr) ? DateTimeOffset.UtcNow
            : DateTimeOffset.TryParse(dateStr, out var r) ? r : DateTimeOffset.UtcNow;
    }

    // ── Environment Agency API response models ───────────────────────

    private record FloodResponse(
        [property: JsonPropertyName("items")] List<FloodItem>? Items);

    private record FloodItem(
        [property: JsonPropertyName("description")]
        string? Description,
        [property: JsonPropertyName("eaAreaName")]
        string? EaAreaName,
        [property: JsonPropertyName("eaRegionName")]
        string? EaRegionName,
        [property: JsonPropertyName("floodArea")]
        FloodArea? FloodArea,
        [property: JsonPropertyName("floodAreaID")]
        string? FloodAreaId,
        [property: JsonPropertyName("isTidal")]
        bool? IsTidal,
        [property: JsonPropertyName("message")]
        string? Message,
        [property: JsonPropertyName("severity")]
        string? Severity,
        [property: JsonPropertyName("severityLevel")]
        int? SeverityLevel,
        [property: JsonPropertyName("timeMessageChanged")]
        string? TimeMessageChanged,
        [property: JsonPropertyName("timeRaised")]
        string? TimeRaised,
        [property: JsonPropertyName("timeSeverityChanged")]
        string? TimeSeverityChanged);

    private record FloodArea(
        [property: JsonPropertyName("county")] string? County,
        [property: JsonPropertyName("riverOrSea")]
        string? RiverOrSea);

    private record StationResponse(
        [property: JsonPropertyName("items")] List<StationItem>? Items);

    private record StationItem(
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("stationReference")]
        string? StationReference,
        [property: JsonPropertyName("riverName")]
        string? RiverName,
        [property: JsonPropertyName("town")] string? Town,
        [property: JsonPropertyName("catchmentName")]
        string? CatchmentName,
        [property: JsonPropertyName("typicalRangeHigh")]
        double? TypicalRangeHigh,
        [property: JsonPropertyName("typicalRangeLow")]
        double? TypicalRangeLow,
        [property: JsonPropertyName("lat")] double? Lat,
        [property: JsonPropertyName("long")] double? Lng);
}
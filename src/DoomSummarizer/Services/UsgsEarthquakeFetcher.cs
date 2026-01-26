using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches earthquake data from USGS GeoJSON feeds — free, no auth.
/// Perfect for doom vibes. Includes magnitude, location, depth, tsunami alerts.
/// https://earthquake.usgs.gov/earthquakes/feed/
/// </summary>
public class UsgsEarthquakeFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary";

    /// <summary>
    /// Available feed types by severity and time window.
    /// </summary>
    private static readonly Dictionary<string, string> Feeds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Significant earthquakes
        ["significant_week"] = $"{BaseUrl}/significant_week.geojson",
        ["significant_month"] = $"{BaseUrl}/significant_month.geojson",

        // M4.5+ (widely felt)
        ["4.5_day"] = $"{BaseUrl}/4.5_day.geojson",
        ["4.5_week"] = $"{BaseUrl}/4.5_week.geojson",

        // M2.5+ (generally felt)
        ["2.5_day"] = $"{BaseUrl}/2.5_day.geojson",
        ["2.5_week"] = $"{BaseUrl}/2.5_week.geojson",

        // All earthquakes
        ["all_hour"] = $"{BaseUrl}/all_hour.geojson",
        ["all_day"] = $"{BaseUrl}/all_day.geojson",
    };

    /// <summary>
    /// Fetch recent earthquakes. Default: significant past week + M4.5 past day.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? feed = null)
    {
        var items = new List<ContentItem>();

        // Use specified feed or combine significant + 4.5 for good coverage
        var feedUrls = new List<string>();
        if (feed != null && Feeds.TryGetValue(feed, out var specificFeed))
        {
            feedUrls.Add(specificFeed);
        }
        else
        {
            feedUrls.Add(Feeds["significant_week"]);
            feedUrls.Add(Feeds["4.5_day"]);
        }

        var seen = new HashSet<string>();

        foreach (var feedUrl in feedUrls)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
                request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var geoJson = JsonSerializer.Deserialize<GeoJsonResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (geoJson?.Features == null) continue;

                foreach (var feature in geoJson.Features)
                {
                    var props = feature.Properties;
                    if (props == null || string.IsNullOrEmpty(props.Place)) continue;

                    var id = feature.Id ?? props.Code ?? props.Place;
                    if (!seen.Add(id)) continue;

                    var mag = props.Mag ?? 0;
                    var tsunami = props.Tsunami > 0 ? " [TSUNAMI WARNING]" : "";
                    var depth = feature.Geometry?.Coordinates?.Length > 2
                        ? feature.Geometry.Coordinates[2] : 0;

                    var title = $"M{mag:F1} Earthquake - {props.Place}{tsunami}";
                    var content = $"Magnitude {mag:F1} earthquake at {props.Place}. " +
                                  $"Depth: {depth:F1} km. " +
                                  (props.Felt > 0 ? $"Felt by {props.Felt} people. " : "") +
                                  (props.Alert != null ? $"Alert level: {props.Alert}. " : "") +
                                  (props.Tsunami > 0 ? "Tsunami warning issued. " : "") +
                                  $"Status: {props.Status ?? "automatic"}.";

                    var time = props.Time > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(props.Time)
                        : DateTimeOffset.UtcNow;

                    items.Add(new ContentItem
                    {
                        Id = $"usgs_{id}",
                        Source = "earthquake",
                        Title = title,
                        Url = props.Url ?? $"https://earthquake.usgs.gov/earthquakes/eventpage/{id}",
                        Content = content,
                        Author = "USGS",
                        Score = (int)(mag * 100), // Higher magnitude = higher score
                        CreatedAt = time
                    });
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: USGS earthquake feed failed: {ex.Message}[/]");
            }
        }

        return items
            .OrderByDescending(i => i.Score)
            .ThenByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Available earthquake feed names for -s earthquake:feed_name syntax.
    /// </summary>
    public static IEnumerable<string> AvailableFeeds => Feeds.Keys;

    // GeoJSON response models
    private record GeoJsonResponse(string? Type, GeoJsonMetadata? Metadata, List<GeoJsonFeature>? Features);
    private record GeoJsonMetadata(long Generated, string? Title, int Count);
    private record GeoJsonFeature(string? Type, string? Id, GeoJsonProperties? Properties, GeoJsonGeometry? Geometry);
    private record GeoJsonGeometry(string? Type, double[]? Coordinates);
    private record GeoJsonProperties(
        double? Mag, string? Place, long Time, long Updated,
        string? Url, string? Detail, int? Felt,
        string? Alert, string? Status, int Tsunami,
        string? Code, string? Type);
}

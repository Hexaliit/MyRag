using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches UK Parliamentary data from the Hansard API — free, no auth.
/// Covers debates, written statements, written answers, and divisions (votes).
/// https://hansard-api.parliament.uk/
/// </summary>
public class UkParliamentFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://hansard-api.parliament.uk";
    private const string UserAgent = "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)";

    /// <summary>
    /// Available sections for sub-parameter routing.
    /// Usage: -s parliament, -s parliament:debates, -s parliament:divisions
    /// </summary>
    public static readonly IReadOnlyList<string> AvailableSections =
        ["debates", "statements", "answers", "divisions"];

    /// <summary>
    /// Search Parliamentary records by query and optional section.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? query = null, string? section = null)
    {
        // If we have a search query, use the search API
        if (!string.IsNullOrWhiteSpace(query))
            return await SearchAsync(query, limit, section);

        // Otherwise fetch recent debates
        return await FetchRecentDebatesAsync(limit);
    }

    /// <summary>
    /// Search Hansard using the search endpoint.
    /// Returns contributions, debates, and divisions matching the query.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int limit = 20, string? section = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var take = Math.Min(limit, 30);
            var url = $"{BaseUrl}/search.json" +
                      $"?queryParameters.searchTerm={Uri.EscapeDataString(query)}" +
                      $"&queryParameters.take={take}";

            var result = await FetchJsonAsync<HansardSearchResponse>(url);
            if (result == null) return items;

            // Debates
            if (section is null or "debates" && result.Debates != null)
            {
                foreach (var debate in result.Debates.Take(limit))
                {
                    if (string.IsNullOrEmpty(debate.Title)) continue;

                    var house = debate.House ?? "Unknown";
                    var date = TryParseDate(debate.SittingDate);

                    items.Add(new ContentItem
                    {
                        Id = $"parl_debate_{GenerateId(debate.Title + debate.SittingDate)}",
                        Source = "parliament",
                        Title = $"[{house}] {debate.Title}",
                        Url = BuildDebateUrl(debate),
                        Content = StripHtml(debate.DebateSection ?? debate.Title),
                        Author = $"UK Parliament ({house})",
                        CreatedAt = date,
                        Tags = ["debate", house.ToLowerInvariant()],
                        Metadata = new Dictionary<string, string>
                        {
                            ["house"] = house,
                            ["type"] = "debate"
                        }
                    });
                }
            }

            // Divisions (votes)
            if (section is null or "divisions" && result.Divisions != null)
            {
                foreach (var div in result.Divisions.Take(Math.Max(5, limit - items.Count)))
                {
                    if (string.IsNullOrEmpty(div.Title)) continue;

                    var house = div.House ?? "Unknown";
                    var date = TryParseDate(div.Date);
                    var ayes = div.AyesCount ?? 0;
                    var noes = div.NoesCount ?? 0;

                    items.Add(new ContentItem
                    {
                        Id = $"parl_div_{GenerateId(div.Title + div.Date)}",
                        Source = "parliament",
                        Title = $"[Vote] {div.Title}",
                        Url = $"https://hansard.parliament.uk",
                        Content = $"Division in {house}: Ayes {ayes}, Noes {noes}. " +
                                  $"Result: {(ayes > noes ? "Passed" : "Rejected")}.",
                        Author = $"UK Parliament ({house})",
                        CreatedAt = date,
                        Score = ayes + noes, // Total votes as engagement metric
                        Tags = ["division", "vote", house.ToLowerInvariant()],
                        Metadata = new Dictionary<string, string>
                        {
                            ["house"] = house,
                            ["type"] = "division",
                            ["ayes"] = ayes.ToString(),
                            ["noes"] = noes.ToString()
                        }
                    });
                }
            }

            // Written Statements
            if (section is "statements" && result.WrittenStatements != null)
            {
                foreach (var stmt in result.WrittenStatements.Take(limit))
                {
                    if (string.IsNullOrEmpty(stmt.Title)) continue;

                    items.Add(new ContentItem
                    {
                        Id = $"parl_stmt_{GenerateId(stmt.Title + stmt.MemberName)}",
                        Source = "parliament",
                        Title = $"[Statement] {stmt.Title}",
                        Url = $"https://hansard.parliament.uk",
                        Content = StripHtml(stmt.Text ?? stmt.Title),
                        Author = stmt.MemberName ?? "UK Parliament",
                        CreatedAt = TryParseDate(stmt.SittingDate),
                        Tags = ["statement"]
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: UK Parliament search failed: {ex.Message}");
        }

        return items.Take(limit).ToList();
    }

    /// <summary>
    /// Fetch recent House of Commons and Lords sittings.
    /// </summary>
    public async Task<List<ContentItem>> FetchRecentDebatesAsync(int limit = 20)
    {
        var items = new List<ContentItem>();

        try
        {
            // Fetch recent sittings from the last 7 days
            var endDate = DateTime.UtcNow;
            var startDate = endDate.AddDays(-7);

            var url = $"{BaseUrl}/search.json" +
                      $"?queryParameters.startDate={startDate:yyyy-MM-dd}" +
                      $"&queryParameters.endDate={endDate:yyyy-MM-dd}" +
                      $"&queryParameters.take={Math.Min(limit, 30)}";

            var result = await FetchJsonAsync<HansardSearchResponse>(url);
            if (result?.Debates == null) return items;

            foreach (var debate in result.Debates.Take(limit))
            {
                if (string.IsNullOrEmpty(debate.Title)) continue;

                var house = debate.House ?? "Unknown";
                items.Add(new ContentItem
                {
                    Id = $"parl_debate_{GenerateId(debate.Title + debate.SittingDate)}",
                    Source = "parliament",
                    Title = $"[{house}] {debate.Title}",
                    Url = BuildDebateUrl(debate),
                    Content = StripHtml(debate.DebateSection ?? debate.Title),
                    Author = $"UK Parliament ({house})",
                    CreatedAt = TryParseDate(debate.SittingDate),
                    Tags = ["debate", house.ToLowerInvariant()]
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: UK Parliament recent debates failed: {ex.Message}");
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

    private static string BuildDebateUrl(HansardDebate debate)
    {
        if (!string.IsNullOrEmpty(debate.SittingDate))
        {
            if (DateTimeOffset.TryParse(debate.SittingDate, out var date))
                return $"https://hansard.parliament.uk/search/Debates?startDate={date:yyyy-MM-dd}&endDate={date:yyyy-MM-dd}";
        }
        return "https://hansard.parliament.uk";
    }

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 2000 ? text[..2000] : text;
    }

    private static string GenerateId(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();

    private static DateTimeOffset TryParseDate(string? dateStr) =>
        string.IsNullOrEmpty(dateStr) ? DateTimeOffset.UtcNow
            : DateTimeOffset.TryParse(dateStr, out var r) ? r : DateTimeOffset.UtcNow;

    // ── Hansard API response models ──────────────────────────────────

    private record HansardSearchResponse(
        [property: JsonPropertyName("TotalDebates")] int? TotalDebates,
        [property: JsonPropertyName("TotalDivisions")] int? TotalDivisions,
        [property: JsonPropertyName("TotalContributions")] int? TotalContributions,
        [property: JsonPropertyName("TotalWrittenStatements")] int? TotalWrittenStatements,
        [property: JsonPropertyName("SearchTerms")] List<string>? SearchTerms,
        [property: JsonPropertyName("Debates")] List<HansardDebate>? Debates,
        [property: JsonPropertyName("Divisions")] List<HansardDivision>? Divisions,
        [property: JsonPropertyName("Contributions")] List<HansardContribution>? Contributions,
        [property: JsonPropertyName("WrittenStatements")] List<HansardContribution>? WrittenStatements);

    private record HansardDebate(
        [property: JsonPropertyName("Title")] string? Title,
        [property: JsonPropertyName("House")] string? House,
        [property: JsonPropertyName("SittingDate")] string? SittingDate,
        [property: JsonPropertyName("DebateSection")] string? DebateSection);

    private record HansardDivision(
        [property: JsonPropertyName("Title")] string? Title,
        [property: JsonPropertyName("House")] string? House,
        [property: JsonPropertyName("Date")] string? Date,
        [property: JsonPropertyName("AyesCount")] int? AyesCount,
        [property: JsonPropertyName("NoesCount")] int? NoesCount);

    private record HansardContribution(
        [property: JsonPropertyName("Title")] string? Title,
        [property: JsonPropertyName("MemberName")] string? MemberName,
        [property: JsonPropertyName("ContributionText")] string? Text,
        [property: JsonPropertyName("SittingDate")] string? SittingDate,
        [property: JsonPropertyName("House")] string? House);
}

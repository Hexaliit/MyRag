using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services.External;

/// <summary>
///     Client for Open Movie Database (OMDB) API.
///     https://www.omdbapi.com/
///     OMDB provides IMDB ratings, Rotten Tomatoes, and Metacritic scores.
/// </summary>
public class OmdbClient : IDisposable
{
    private const string BaseUrl = "https://www.omdbapi.com/";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<OmdbClient> _logger;
    private readonly OmdbOptions _options;

    public OmdbClient(
        HttpClient httpClient,
        IOptions<OmdbOptions> options,
        ILogger<OmdbClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public void Dispose()
    {
        // HttpClient managed by DI
    }

    /// <summary>
    ///     Get movie/TV show by IMDB ID.
    /// </summary>
    public async Task<OmdbResult?> GetByImdbIdAsync(string imdbId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OMDB API key not configured");
            return null;
        }

        try
        {
            var url = $"{BaseUrl}?apikey={_options.ApiKey}&i={imdbId}&plot=full";
            var response = await _httpClient.GetFromJsonAsync<OmdbResult>(url, _jsonOptions, ct);

            if (response?.Response == "False")
            {
                _logger.LogWarning("OMDB returned error for {ImdbId}: {Error}", imdbId, response.Error);
                return null;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get OMDB data for IMDB ID: {ImdbId}", imdbId);
            return null;
        }
    }

    /// <summary>
    ///     Search for movies/TV shows by title.
    /// </summary>
    public async Task<List<OmdbSearchResult>> SearchAsync(
        string query,
        int? year = null,
        string? type = null, // movie, series, episode
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OMDB API key not configured");
            return [];
        }

        try
        {
            var url = $"{BaseUrl}?apikey={_options.ApiKey}&s={Uri.EscapeDataString(query)}";
            if (year.HasValue) url += $"&y={year}";
            if (!string.IsNullOrEmpty(type)) url += $"&type={type}";

            var response = await _httpClient.GetFromJsonAsync<OmdbSearchResponse>(url, _jsonOptions, ct);

            if (response?.Response == "False")
            {
                _logger.LogDebug("OMDB search returned no results for: {Query}", query);
                return [];
            }

            return response?.Search ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search OMDB for: {Query}", query);
            return [];
        }
    }

    /// <summary>
    ///     Get by title (exact match).
    /// </summary>
    public async Task<OmdbResult?> GetByTitleAsync(
        string title,
        int? year = null,
        string? type = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OMDB API key not configured");
            return null;
        }

        try
        {
            var url = $"{BaseUrl}?apikey={_options.ApiKey}&t={Uri.EscapeDataString(title)}&plot=full";
            if (year.HasValue) url += $"&y={year}";
            if (!string.IsNullOrEmpty(type)) url += $"&type={type}";

            var response = await _httpClient.GetFromJsonAsync<OmdbResult>(url, _jsonOptions, ct);

            if (response?.Response == "False")
            {
                _logger.LogDebug("OMDB returned no result for title: {Title}", title);
                return null;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get OMDB data for title: {Title}", title);
            return null;
        }
    }

    /// <summary>
    ///     Convert OMDB result to unified metadata format.
    /// </summary>
    public ExternalMediaMetadata ToExternalMetadata(OmdbResult omdb)
    {
        // Parse ratings
        double? imdbRating = null;
        int? imdbVotes = null;
        int? rottenTomatoes = null;

        if (!string.IsNullOrEmpty(omdb.ImdbRating) && omdb.ImdbRating != "N/A")
            if (double.TryParse(omdb.ImdbRating, out var rating))
                imdbRating = rating;

        if (!string.IsNullOrEmpty(omdb.ImdbVotes) && omdb.ImdbVotes != "N/A")
            if (int.TryParse(omdb.ImdbVotes.Replace(",", ""), out var votes))
                imdbVotes = votes;

        // Parse Rotten Tomatoes from Ratings array
        var rtRating = omdb.Ratings?.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
        if (rtRating != null && rtRating.Value?.EndsWith("%") == true)
            if (int.TryParse(rtRating.Value.TrimEnd('%'), out var rt))
                rottenTomatoes = rt;

        // Parse runtime
        int? runtime = null;
        if (!string.IsNullOrEmpty(omdb.Runtime) && omdb.Runtime != "N/A")
        {
            var runtimeStr = omdb.Runtime.Replace(" min", "");
            if (int.TryParse(runtimeStr, out var r)) runtime = r;
        }

        // Parse year
        int? year = null;
        if (!string.IsNullOrEmpty(omdb.Year) && omdb.Year != "N/A")
        {
            // Handle ranges like "2015-2023"
            var yearStr = omdb.Year.Split('–', '-')[0];
            if (int.TryParse(yearStr, out var y)) year = y;
        }

        // Parse release date
        DateOnly? releaseDate = null;
        if (!string.IsNullOrEmpty(omdb.Released) && omdb.Released != "N/A")
            if (DateOnly.TryParse(omdb.Released, out var d))
                releaseDate = d;

        return new ExternalMediaMetadata
        {
            Source = "OMDB",
            ExternalId = omdb.ImdbId ?? "",
            Title = omdb.Title ?? "Unknown",
            Overview = omdb.Plot,
            Year = year,
            ReleaseDate = releaseDate,
            RuntimeMinutes = runtime,
            Genres = ParseCommaSeparated(omdb.Genre),
            Directors = ParseCommaSeparated(omdb.Director),
            Writers = ParseCommaSeparated(omdb.Writer),
            Cast = ParseCommaSeparated(omdb.Actors).Select(a => new CastMember { Name = a }).ToList(),
            ImdbRating = imdbRating,
            ImdbVotes = imdbVotes,
            RottenTomatoesScore = rottenTomatoes,
            PosterUrl = omdb.Poster != "N/A" ? omdb.Poster : null,
            Countries = ParseCommaSeparated(omdb.Country),
            Languages = ParseCommaSeparated(omdb.Language),
            ImdbId = omdb.ImdbId,
            ContentRating = omdb.Rated != "N/A" ? omdb.Rated : null
        };
    }

    private static List<string> ParseCommaSeparated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "N/A") return [];

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
    }
}

public class OmdbOptions
{
    public string? ApiKey { get; set; }
}

// Response models
public record OmdbSearchResponse
{
    public List<OmdbSearchResult>? Search { get; init; }
    public string? TotalResults { get; init; }
    public string? Response { get; init; }
    public string? Error { get; init; }
}

public record OmdbSearchResult
{
    public string? Title { get; init; }
    public string? Year { get; init; }
    public string? ImdbId { get; init; }
    public string? Type { get; init; }
    public string? Poster { get; init; }
}

public record OmdbResult
{
    public string? Title { get; init; }
    public string? Year { get; init; }
    public string? Rated { get; init; }
    public string? Released { get; init; }
    public string? Runtime { get; init; }
    public string? Genre { get; init; }
    public string? Director { get; init; }
    public string? Writer { get; init; }
    public string? Actors { get; init; }
    public string? Plot { get; init; }
    public string? Language { get; init; }
    public string? Country { get; init; }
    public string? Awards { get; init; }
    public string? Poster { get; init; }
    public List<OmdbRating>? Ratings { get; init; }
    public string? Metascore { get; init; }
    public string? ImdbRating { get; init; }
    public string? ImdbVotes { get; init; }
    public string? ImdbId { get; init; }
    public string? Type { get; init; }
    public string? TotalSeasons { get; init; }
    public string? Response { get; init; }
    public string? Error { get; init; }

    // DVD release info
    public string? Dvd { get; init; }
    public string? BoxOffice { get; init; }
    public string? Production { get; init; }
    public string? Website { get; init; }
}

public record OmdbRating
{
    public string? Source { get; init; }
    public string? Value { get; init; }
}
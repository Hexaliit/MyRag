using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services.External;

/// <summary>
///     Client for The Movie Database (TMDB) API.
///     https://www.themoviedb.org/documentation/api
/// </summary>
public class TmdbClient : IDisposable
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<TmdbClient> _logger;
    private readonly TmdbOptions _options;

    public TmdbClient(
        HttpClient httpClient,
        IOptions<TmdbOptions> options,
        ILogger<TmdbClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };
    }

    public void Dispose()
    {
        // HttpClient is managed by DI, don't dispose
    }

    /// <summary>
    ///     Search for movies by title.
    /// </summary>
    public async Task<List<TmdbSearchResult>> SearchMoviesAsync(
        string query,
        int? year = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("TMDB API key not configured");
            return [];
        }

        try
        {
            var url = $"{BaseUrl}/search/movie?api_key={_options.ApiKey}&query={Uri.EscapeDataString(query)}";
            if (year.HasValue) url += $"&year={year}";

            var response = await _httpClient.GetFromJsonAsync<TmdbSearchResponse>(url, _jsonOptions, ct);
            return response?.Results ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search TMDB for: {Query}", query);
            return [];
        }
    }

    /// <summary>
    ///     Search for TV shows by title.
    /// </summary>
    public async Task<List<TmdbTvSearchResult>> SearchTvShowsAsync(
        string query,
        int? year = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("TMDB API key not configured");
            return [];
        }

        try
        {
            var url = $"{BaseUrl}/search/tv?api_key={_options.ApiKey}&query={Uri.EscapeDataString(query)}";
            if (year.HasValue) url += $"&first_air_date_year={year}";

            var response = await _httpClient.GetFromJsonAsync<TmdbTvSearchResponse>(url, _jsonOptions, ct);
            return response?.Results ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search TMDB TV for: {Query}", query);
            return [];
        }
    }

    /// <summary>
    ///     Get detailed movie information.
    /// </summary>
    public async Task<TmdbMovieDetails?> GetMovieDetailsAsync(int tmdbId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        try
        {
            var url =
                $"{BaseUrl}/movie/{tmdbId}?api_key={_options.ApiKey}&append_to_response=credits,keywords,external_ids";
            return await _httpClient.GetFromJsonAsync<TmdbMovieDetails>(url, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TMDB movie details for ID: {TmdbId}", tmdbId);
            return null;
        }
    }

    /// <summary>
    ///     Get detailed TV show information.
    /// </summary>
    public async Task<TmdbTvDetails?> GetTvDetailsAsync(int tmdbId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        try
        {
            var url =
                $"{BaseUrl}/tv/{tmdbId}?api_key={_options.ApiKey}&append_to_response=credits,keywords,external_ids";
            return await _httpClient.GetFromJsonAsync<TmdbTvDetails>(url, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TMDB TV details for ID: {TmdbId}", tmdbId);
            return null;
        }
    }

    /// <summary>
    ///     Get TV episode details.
    /// </summary>
    public async Task<TmdbEpisodeDetails?> GetEpisodeDetailsAsync(
        int tvId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        try
        {
            var url =
                $"{BaseUrl}/tv/{tvId}/season/{seasonNumber}/episode/{episodeNumber}?api_key={_options.ApiKey}&append_to_response=credits";
            return await _httpClient.GetFromJsonAsync<TmdbEpisodeDetails>(url, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TMDB episode details for {TvId} S{Season}E{Episode}",
                tvId, seasonNumber, episodeNumber);
            return null;
        }
    }

    /// <summary>
    ///     Get person (actor) details including profile images.
    /// </summary>
    public async Task<TmdbPersonDetails?> GetPersonDetailsAsync(int personId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        try
        {
            var url = $"{BaseUrl}/person/{personId}?api_key={_options.ApiKey}&append_to_response=images,external_ids";
            return await _httpClient.GetFromJsonAsync<TmdbPersonDetails>(url, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TMDB person details for ID: {PersonId}", personId);
            return null;
        }
    }

    /// <summary>
    ///     Convert TMDB movie to unified metadata format.
    /// </summary>
    public ExternalMediaMetadata ToExternalMetadata(TmdbMovieDetails movie)
    {
        return new ExternalMediaMetadata
        {
            Source = "TMDB",
            ExternalId = movie.Id.ToString(),
            Title = movie.Title ?? "Unknown",
            OriginalTitle = movie.OriginalTitle,
            Overview = movie.Overview,
            Year = movie.ReleaseDate?.Year,
            ReleaseDate = movie.ReleaseDate,
            RuntimeMinutes = movie.Runtime,
            Genres = movie.Genres?.Select(g => g.Name).Where(n => n != null).Cast<string>().ToList() ?? [],
            Directors = movie.Credits?.Crew?.Where(c => c.Job == "Director").Select(c => c.Name).Where(n => n != null)
                .Cast<string>().ToList() ?? [],
            Writers = movie.Credits?.Crew?.Where(c => c.Department == "Writing").Select(c => c.Name)
                .Where(n => n != null).Cast<string>().ToList() ?? [],
            Cast = movie.Credits?.Cast?.Take(20).Select(c => new CastMember
            {
                Name = c.Name ?? "Unknown",
                Character = c.Character,
                ProfileUrl = c.ProfilePath != null ? $"{ImageBaseUrl}/w185{c.ProfilePath}" : null,
                Order = c.Order
            }).ToList() ?? [],
            TmdbRating = movie.VoteAverage,
            PosterUrl = movie.PosterPath != null ? $"{ImageBaseUrl}/w500{movie.PosterPath}" : null,
            BackdropUrl = movie.BackdropPath != null ? $"{ImageBaseUrl}/w1280{movie.BackdropPath}" : null,
            Tagline = movie.Tagline,
            Countries = movie.ProductionCountries?.Select(c => c.Name).Where(n => n != null).Cast<string>().ToList() ??
                        [],
            Languages =
                movie.SpokenLanguages?.Select(l => l.EnglishName).Where(n => n != null).Cast<string>().ToList() ?? [],
            ImdbId = movie.ExternalIds?.ImdbId,
            Budget = movie.Budget,
            Revenue = movie.Revenue,
            Keywords = movie.Keywords?.Keywords?.Select(k => k.Name).Where(n => n != null).Cast<string>().ToList() ?? []
        };
    }

    /// <summary>
    ///     Get full poster URL.
    /// </summary>
    public static string? GetPosterUrl(string? posterPath, string size = "w500")
    {
        return posterPath != null ? $"{ImageBaseUrl}/{size}{posterPath}" : null;
    }

    /// <summary>
    ///     Get full profile URL.
    /// </summary>
    public static string? GetProfileUrl(string? profilePath, string size = "w185")
    {
        return profilePath != null ? $"{ImageBaseUrl}/{size}{profilePath}" : null;
    }
}

public class TmdbOptions
{
    public string? ApiKey { get; set; }
    public string? ReadAccessToken { get; set; }
}

// Response models
public record TmdbSearchResponse
{
    public int Page { get; init; }
    public List<TmdbSearchResult>? Results { get; init; }
    public int TotalPages { get; init; }
    public int TotalResults { get; init; }
}

public record TmdbSearchResult
{
    public int Id { get; init; }
    public string? Title { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
    public double Popularity { get; init; }
    public List<int>? GenreIds { get; init; }
    public string? OriginalLanguage { get; init; }
    public bool Adult { get; init; }
    public bool Video { get; init; }
}

public record TmdbTvSearchResponse
{
    public int Page { get; init; }
    public List<TmdbTvSearchResult>? Results { get; init; }
    public int TotalPages { get; init; }
    public int TotalResults { get; init; }
}

public record TmdbTvSearchResult
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? OriginalName { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public DateOnly? FirstAirDate { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
    public double Popularity { get; init; }
    public List<int>? GenreIds { get; init; }
    public List<string>? OriginCountry { get; init; }
    public string? OriginalLanguage { get; init; }
}

public record TmdbMovieDetails
{
    public int Id { get; init; }
    public string? Title { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Runtime { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
    public long Budget { get; init; }
    public long Revenue { get; init; }
    public string? Tagline { get; init; }
    public string? Status { get; init; }
    public string? Homepage { get; init; }
    public string? ImdbId { get; init; }
    public List<TmdbGenre>? Genres { get; init; }
    public List<TmdbProductionCountry>? ProductionCountries { get; init; }
    public List<TmdbSpokenLanguage>? SpokenLanguages { get; init; }
    public TmdbCredits? Credits { get; init; }
    public TmdbKeywords? Keywords { get; init; }
    public TmdbExternalIds? ExternalIds { get; init; }
}

public record TmdbTvDetails
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? OriginalName { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public DateOnly? FirstAirDate { get; init; }
    public DateOnly? LastAirDate { get; init; }
    public int NumberOfSeasons { get; init; }
    public int NumberOfEpisodes { get; init; }
    public List<int>? EpisodeRunTime { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
    public string? Status { get; init; }
    public string? Type { get; init; }
    public string? Tagline { get; init; }
    public string? Homepage { get; init; }
    public List<TmdbGenre>? Genres { get; init; }
    public List<TmdbNetwork>? Networks { get; init; }
    public List<string>? OriginCountry { get; init; }
    public List<TmdbSpokenLanguage>? SpokenLanguages { get; init; }
    public TmdbCredits? Credits { get; init; }
    public TmdbExternalIds? ExternalIds { get; init; }
}

public record TmdbEpisodeDetails
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Overview { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public DateOnly? AirDate { get; init; }
    public int? Runtime { get; init; }
    public string? StillPath { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
    public TmdbCredits? Credits { get; init; }
}

public record TmdbPersonDetails
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Biography { get; init; }
    public string? Birthday { get; init; }
    public string? Deathday { get; init; }
    public string? PlaceOfBirth { get; init; }
    public string? ProfilePath { get; init; }
    public string? ImdbId { get; init; }
    public string? Homepage { get; init; }
    public int Gender { get; init; }
    public string? KnownForDepartment { get; init; }
    public double Popularity { get; init; }
    public TmdbPersonImages? Images { get; init; }
    public TmdbExternalIds? ExternalIds { get; init; }
}

public record TmdbPersonImages
{
    public List<TmdbImage>? Profiles { get; init; }
}

public record TmdbImage
{
    public string? FilePath { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double AspectRatio { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
}

public record TmdbGenre
{
    public int Id { get; init; }
    public string? Name { get; init; }
}

public record TmdbProductionCountry
{
    public string? Iso31661 { get; init; }
    public string? Name { get; init; }
}

public record TmdbSpokenLanguage
{
    public string? Iso6391 { get; init; }
    public string? Name { get; init; }
    public string? EnglishName { get; init; }
}

public record TmdbNetwork
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? LogoPath { get; init; }
    public string? OriginCountry { get; init; }
}

public record TmdbCredits
{
    public List<TmdbCastMember>? Cast { get; init; }
    public List<TmdbCrewMember>? Crew { get; init; }
}

public record TmdbCastMember
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? OriginalName { get; init; }
    public string? Character { get; init; }
    public string? ProfilePath { get; init; }
    public int Order { get; init; }
    public int Gender { get; init; }
    public string? KnownForDepartment { get; init; }
}

public record TmdbCrewMember
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? OriginalName { get; init; }
    public string? Job { get; init; }
    public string? Department { get; init; }
    public string? ProfilePath { get; init; }
    public int Gender { get; init; }
}

public record TmdbKeywords
{
    public List<TmdbKeyword>? Keywords { get; init; }
}

public record TmdbKeyword
{
    public int Id { get; init; }
    public string? Name { get; init; }
}

public record TmdbExternalIds
{
    public string? ImdbId { get; init; }
    public string? FacebookId { get; init; }
    public string? InstagramId { get; init; }
    public string? TwitterId { get; init; }
    public int? TvdbId { get; init; }
    public string? WikidataId { get; init; }
}
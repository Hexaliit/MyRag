using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VideoSummarizer.Core.Services.External;

/// <summary>
/// Client for OpenSubtitles.com REST API.
/// https://opensubtitles.stoplight.io/docs/opensubtitles-api
/// </summary>
public class OpenSubtitlesClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenSubtitlesClient> _logger;
    private readonly OpenSubtitlesOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    private string? _jwtToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";

    public OpenSubtitlesClient(
        HttpClient httpClient,
        IOptions<OpenSubtitlesOptions> options,
        ILogger<OpenSubtitlesClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        // Set default headers
        _httpClient.DefaultRequestHeaders.Add("Api-Key", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", _options.UserAgent ?? "LucidRAG v1.0");
    }

    /// <summary>
    /// Search for subtitles by IMDB ID.
    /// </summary>
    public async Task<List<OpenSubtitlesResult>> SearchByImdbIdAsync(
        string imdbId,
        string? language = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OpenSubtitles API key not configured");
            return [];
        }

        try
        {
            var url = $"{BaseUrl}/subtitles?imdb_id={imdbId.TrimStart('t')}";
            if (!string.IsNullOrEmpty(language))
            {
                url += $"&languages={language}";
            }

            var response = await _httpClient.GetFromJsonAsync<OpenSubtitlesSearchResponse>(url, _jsonOptions, ct);
            return response?.Data ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search OpenSubtitles by IMDB ID: {ImdbId}", imdbId);
            return [];
        }
    }

    /// <summary>
    /// Search for subtitles by file hash (more accurate).
    /// Uses OpenSubtitles hash algorithm.
    /// </summary>
    public async Task<List<OpenSubtitlesResult>> SearchByHashAsync(
        string filePath,
        string? language = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OpenSubtitles API key not configured");
            return [];
        }

        try
        {
            var hash = ComputeOpenSubtitlesHash(filePath);
            var fileInfo = new FileInfo(filePath);

            var url = $"{BaseUrl}/subtitles?moviehash={hash}&moviebytesize={fileInfo.Length}";
            if (!string.IsNullOrEmpty(language))
            {
                url += $"&languages={language}";
            }

            var response = await _httpClient.GetFromJsonAsync<OpenSubtitlesSearchResponse>(url, _jsonOptions, ct);
            return response?.Data ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search OpenSubtitles by hash for: {FilePath}", filePath);
            return [];
        }
    }

    /// <summary>
    /// Search for subtitles by query (title).
    /// </summary>
    public async Task<List<OpenSubtitlesResult>> SearchByQueryAsync(
        string query,
        int? year = null,
        string? language = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OpenSubtitles API key not configured");
            return [];
        }

        try
        {
            var url = $"{BaseUrl}/subtitles?query={Uri.EscapeDataString(query)}";
            if (year.HasValue)
            {
                url += $"&year={year}";
            }
            if (!string.IsNullOrEmpty(language))
            {
                url += $"&languages={language}";
            }

            var response = await _httpClient.GetFromJsonAsync<OpenSubtitlesSearchResponse>(url, _jsonOptions, ct);
            return response?.Data ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search OpenSubtitles for: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Download subtitle file.
    /// Requires login for download links.
    /// </summary>
    public async Task<string?> DownloadSubtitleAsync(
        int fileId,
        string outputPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OpenSubtitles API key not configured");
            return null;
        }

        try
        {
            // Ensure we're logged in
            await EnsureLoggedInAsync(ct);

            // Request download link
            var downloadRequest = new { file_id = fileId };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/download")
            {
                Content = JsonContent.Create(downloadRequest, options: _jsonOptions)
            };

            if (!string.IsNullOrEmpty(_jwtToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
            }

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var downloadResponse = await response.Content.ReadFromJsonAsync<OpenSubtitlesDownloadResponse>(_jsonOptions, ct);
            if (downloadResponse?.Link == null)
            {
                _logger.LogWarning("No download link returned for file ID: {FileId}", fileId);
                return null;
            }

            // Download the actual subtitle file
            var subtitleContent = await _httpClient.GetStringAsync(downloadResponse.Link, ct);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write to file
            await File.WriteAllTextAsync(outputPath, subtitleContent, ct);

            _logger.LogInformation("Downloaded subtitle to: {OutputPath}", outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download subtitle file ID: {FileId}", fileId);
            return null;
        }
    }

    /// <summary>
    /// Login to get JWT token for downloads.
    /// </summary>
    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        // Skip if token still valid
        if (!string.IsNullOrEmpty(_jwtToken) && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
        {
            _logger.LogDebug("OpenSubtitles credentials not configured, skipping login");
            return;
        }

        try
        {
            var loginRequest = new
            {
                username = _options.Username,
                password = _options.Password
            };

            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/login", loginRequest, _jsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var loginResponse = await response.Content.ReadFromJsonAsync<OpenSubtitlesLoginResponse>(_jsonOptions, ct);
            if (loginResponse?.Token != null)
            {
                _jwtToken = loginResponse.Token;
                _tokenExpiry = DateTimeOffset.UtcNow.AddHours(23); // Token valid for 24 hours
                _logger.LogInformation("Logged in to OpenSubtitles as: {Username}", _options.Username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to login to OpenSubtitles");
        }
    }

    /// <summary>
    /// Compute OpenSubtitles hash for a video file.
    /// Algorithm: 64-bit checksum of first and last 64KB + file size.
    /// </summary>
    public static string ComputeOpenSubtitlesHash(string filePath)
    {
        const int ChunkSize = 65536; // 64KB

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        long streamSize = stream.Length;

        // Hash is initialized with file size
        ulong hash = (ulong)streamSize;

        // Read first 64KB
        byte[] buffer = new byte[ChunkSize];
        int bytesRead = stream.Read(buffer, 0, ChunkSize);
        for (int i = 0; i < bytesRead / 8; i++)
        {
            hash += BitConverter.ToUInt64(buffer, i * 8);
        }

        // Seek to last 64KB
        if (streamSize > ChunkSize)
        {
            stream.Seek(-ChunkSize, SeekOrigin.End);
            bytesRead = stream.Read(buffer, 0, ChunkSize);
            for (int i = 0; i < bytesRead / 8; i++)
            {
                hash += BitConverter.ToUInt64(buffer, i * 8);
            }
        }

        return hash.ToString("x16");
    }

    /// <summary>
    /// Find best matching subtitle from search results.
    /// </summary>
    public OpenSubtitlesResult? FindBestMatch(
        List<OpenSubtitlesResult> results,
        string preferredLanguage = "en",
        bool preferHearingImpaired = false)
    {
        if (results.Count == 0)
        {
            return null;
        }

        return results
            .OrderByDescending(r =>
            {
                int score = 0;

                // Language match
                if (r.Attributes?.Language == preferredLanguage)
                    score += 100;

                // Hearing impaired preference
                if (r.Attributes?.HearingImpaired == preferHearingImpaired)
                    score += 10;

                // Not machine translated
                if (r.Attributes?.MachineTranslated == false)
                    score += 20;

                // Download count as popularity
                score += Math.Min(r.Attributes?.DownloadCount ?? 0, 50);

                // Feature film release
                if (r.Attributes?.FeatureDetails?.FeatureType == "Movie")
                    score += 5;

                return score;
            })
            .FirstOrDefault();
    }

    public void Dispose()
    {
        // HttpClient managed by DI
    }
}

public class OpenSubtitlesOptions
{
    public string? ApiKey { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? UserAgent { get; set; } = "LucidRAG v1.0";
}

// Response models
public record OpenSubtitlesSearchResponse
{
    public int TotalPages { get; init; }
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public List<OpenSubtitlesResult>? Data { get; init; }
}

public record OpenSubtitlesResult
{
    public string? Id { get; init; }
    public string? Type { get; init; }
    public OpenSubtitlesAttributes? Attributes { get; init; }
}

public record OpenSubtitlesAttributes
{
    public string? SubtitleId { get; init; }
    public string? Language { get; init; }
    public int DownloadCount { get; init; }
    public int NewDownloadCount { get; init; }
    public bool HearingImpaired { get; init; }
    public bool Hd { get; init; }
    public double? Fps { get; init; }
    public int Votes { get; init; }
    public double Ratings { get; init; }
    public bool FromTrusted { get; init; }
    public bool ForeignPartsOnly { get; init; }
    public bool MachineTranslated { get; init; }
    public string? AiTranslated { get; init; }
    public string? UploadDate { get; init; }
    public string? Release { get; init; }
    public string? Comments { get; init; }
    public int? LegacySubtitleId { get; init; }
    public OpenSubtitlesUploader? Uploader { get; init; }
    public OpenSubtitlesFeatureDetails? FeatureDetails { get; init; }
    public string? Url { get; init; }
    public List<OpenSubtitlesFile>? Files { get; init; }
}

public record OpenSubtitlesUploader
{
    public int? UploaderId { get; init; }
    public string? Name { get; init; }
    public string? Rank { get; init; }
}

public record OpenSubtitlesFeatureDetails
{
    public int? FeatureId { get; init; }
    public string? FeatureType { get; init; }
    public int? Year { get; init; }
    public string? Title { get; init; }
    public string? MovieName { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? ParentImdbId { get; init; }
    public string? ParentTitle { get; init; }
}

public record OpenSubtitlesFile
{
    public int FileId { get; init; }
    public int CdNumber { get; init; }
    public string? FileName { get; init; }
}

public record OpenSubtitlesDownloadResponse
{
    public string? Link { get; init; }
    public string? FileName { get; init; }
    public int Requests { get; init; }
    public int Remaining { get; init; }
    public string? Message { get; init; }
    public string? ResetTime { get; init; }
    public string? ResetTimeUtc { get; init; }
}

public record OpenSubtitlesLoginResponse
{
    public string? Token { get; init; }
    public OpenSubtitlesUser? User { get; init; }
    public string? Status { get; init; }
}

public record OpenSubtitlesUser
{
    public int AllowedDownloads { get; init; }
    public int AllowedTranslations { get; init; }
    public string? Level { get; init; }
    public int UserId { get; init; }
    public bool ExtInstalled { get; init; }
    public bool Vip { get; init; }
}

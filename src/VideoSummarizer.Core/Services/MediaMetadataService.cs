using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services.External;

namespace VideoSummarizer.Core.Services;

/// <summary>
///     Orchestrates metadata lookup from multiple sources (TMDB, OMDB, OpenSubtitles).
///     Provides unified metadata enrichment for media files.
///     External clients are optional - if not configured, metadata features are disabled.
/// </summary>
public class MediaMetadataService
{
    private readonly ILogger<MediaMetadataService> _logger;
    private readonly OmdbClient? _omdb;
    private readonly OpenSubtitlesClient? _openSubtitles;
    private readonly TmdbClient? _tmdb;

    public MediaMetadataService(
        ILogger<MediaMetadataService> logger,
        TmdbClient? tmdb = null,
        OmdbClient? omdb = null,
        OpenSubtitlesClient? openSubtitles = null)
    {
        _tmdb = tmdb;
        _omdb = omdb;
        _openSubtitles = openSubtitles;
        _logger = logger;
    }

    /// <summary>
    ///     Whether external metadata services are available.
    /// </summary>
    public bool HasExternalServices => _tmdb != null || _omdb != null;

    /// <summary>
    ///     Whether subtitle download services are available.
    /// </summary>
    public bool HasSubtitleServices => _openSubtitles != null;

    /// <summary>
    ///     Enrich a media file with external metadata.
    /// </summary>
    public async Task<MediaFile> EnrichMetadataAsync(
        MediaFile mediaFile,
        CancellationToken ct = default)
    {
        if (!HasExternalServices)
        {
            _logger.LogDebug("External metadata services not configured, skipping enrichment");
            return mediaFile;
        }

        if (string.IsNullOrEmpty(mediaFile.ParsedTitle))
        {
            _logger.LogWarning("Cannot enrich metadata: no parsed title for {File}", mediaFile.FileName);
            return mediaFile;
        }

        try
        {
            // First try TMDB (more comprehensive data)
            var tmdbMetadata = await SearchTmdbAsync(
                mediaFile.ParsedTitle,
                mediaFile.ParsedYear,
                ct);

            if (tmdbMetadata != null)
            {
                mediaFile.ExternalMetadata = tmdbMetadata;

                // Supplement with OMDB for additional ratings
                if (_omdb != null && !string.IsNullOrEmpty(tmdbMetadata.ImdbId))
                {
                    var omdbData = await _omdb.GetByImdbIdAsync(tmdbMetadata.ImdbId, ct);
                    if (omdbData != null) mediaFile.ExternalMetadata = MergeOmdbData(tmdbMetadata, omdbData);
                }

                _logger.LogInformation("Enriched {File} with metadata from TMDB: {Title}",
                    mediaFile.FileName, tmdbMetadata.Title);
            }
            else if (_omdb != null)
            {
                // Fallback to OMDB
                var omdbResult = await _omdb.GetByTitleAsync(
                    mediaFile.ParsedTitle,
                    mediaFile.ParsedYear,
                    ct: ct);

                if (omdbResult != null)
                {
                    mediaFile.ExternalMetadata = _omdb.ToExternalMetadata(omdbResult);
                    _logger.LogInformation("Enriched {File} with metadata from OMDB: {Title}",
                        mediaFile.FileName, omdbResult.Title);
                }
            }

            mediaFile.Status = MediaFileStatus.FetchingMetadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enrich metadata for {File}", mediaFile.FileName);
        }

        return mediaFile;
    }

    /// <summary>
    ///     Search TMDB for movie or TV show.
    /// </summary>
    private async Task<ExternalMediaMetadata?> SearchTmdbAsync(
        string title,
        int? year,
        CancellationToken ct)
    {
        if (_tmdb == null)
            return null;

        // Try movie search first
        var movieResults = await _tmdb.SearchMoviesAsync(title, year, ct);
        if (movieResults.Count > 0)
        {
            var bestMatch = FindBestMatch(movieResults, title, year);
            if (bestMatch != null)
            {
                var details = await _tmdb.GetMovieDetailsAsync(bestMatch.Id, ct);
                if (details != null) return _tmdb.ToExternalMetadata(details);
            }
        }

        // Try TV search
        var tvResults = await _tmdb.SearchTvShowsAsync(title, year, ct);
        if (tvResults.Count > 0)
            // TODO: Handle TV show metadata
            _logger.LogDebug("Found TV show match for {Title}, TV handling not yet implemented", title);

        return null;
    }

    /// <summary>
    ///     Find the best matching result from search results.
    /// </summary>
    private TmdbSearchResult? FindBestMatch(
        List<TmdbSearchResult> results,
        string searchTitle,
        int? searchYear)
    {
        if (results.Count == 0)
            return null;

        // Score each result
        var scored = results.Select(r =>
            {
                double score = 0;

                // Title similarity
                var titleSimilarity = ComputeTitleSimilarity(
                    searchTitle,
                    r.Title ?? r.OriginalTitle ?? "");
                score += titleSimilarity * 50;

                // Year match
                if (searchYear.HasValue && r.ReleaseDate?.Year == searchYear.Value)
                {
                    score += 30;
                }
                else if (searchYear.HasValue && r.ReleaseDate.HasValue)
                {
                    var yearDiff = Math.Abs(r.ReleaseDate.Value.Year - searchYear.Value);
                    score += Math.Max(0, 20 - yearDiff * 5);
                }

                // Popularity boost
                score += Math.Min(r.Popularity / 10, 10);

                // Vote count boost (more votes = more reliable)
                score += Math.Min(r.VoteCount / 1000.0, 10);

                return (Result: r, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Return best match if score is above threshold
        var best = scored.FirstOrDefault();
        if (best.Score >= 40) return best.Result;

        _logger.LogDebug("No confident TMDB match for '{Title}' ({Year}), best score: {Score}",
            searchTitle, searchYear, best.Score);
        return null;
    }

    /// <summary>
    ///     Compute title similarity score (0-1).
    /// </summary>
    private static double ComputeTitleSimilarity(string title1, string title2)
    {
        // Normalize titles
        var t1 = NormalizeTitle(title1);
        var t2 = NormalizeTitle(title2);

        if (t1 == t2)
            return 1.0;

        if (t1.Contains(t2) || t2.Contains(t1))
            return 0.9;

        // Simple word overlap
        var words1 = t1.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var words2 = t2.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (words1.Count == 0 || words2.Count == 0)
            return 0;

        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();

        return (double)intersection / union;
    }

    private static string NormalizeTitle(string title)
    {
        return title
            .ToLowerInvariant()
            .Replace(":", "")
            .Replace("-", " ")
            .Replace("'", "")
            .Replace(".", " ")
            .Trim();
    }

    /// <summary>
    ///     Merge OMDB data into TMDB metadata (for ratings).
    /// </summary>
    private static ExternalMediaMetadata MergeOmdbData(ExternalMediaMetadata tmdb, OmdbResult omdb)
    {
        var imdbRating = tmdb.ImdbRating;
        var imdbVotes = tmdb.ImdbVotes;
        var rtScore = tmdb.RottenTomatoesScore;

        // Parse IMDB rating from OMDB
        if (!string.IsNullOrEmpty(omdb.ImdbRating) && omdb.ImdbRating != "N/A")
            if (double.TryParse(omdb.ImdbRating, out var rating))
                imdbRating = rating;

        // Parse IMDB votes from OMDB
        if (!string.IsNullOrEmpty(omdb.ImdbVotes) && omdb.ImdbVotes != "N/A")
            if (int.TryParse(omdb.ImdbVotes.Replace(",", ""), out var votes))
                imdbVotes = votes;

        // Parse Rotten Tomatoes
        var rtRating = omdb.Ratings?.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
        if (rtRating != null && rtRating.Value?.EndsWith("%") == true)
            if (int.TryParse(rtRating.Value.TrimEnd('%'), out var rt))
                rtScore = rt;

        return tmdb with
        {
            ImdbRating = imdbRating,
            ImdbVotes = imdbVotes,
            RottenTomatoesScore = rtScore,
            ContentRating = !string.IsNullOrEmpty(omdb.Rated) && omdb.Rated != "N/A"
                ? omdb.Rated
                : tmdb.ContentRating
        };
    }

    /// <summary>
    ///     Download subtitles for a media file.
    /// </summary>
    public async Task<List<SubtitleFile>> DownloadSubtitlesAsync(
        MediaFile mediaFile,
        List<string> languages,
        CancellationToken ct = default)
    {
        var subtitles = new List<SubtitleFile>();

        if (_openSubtitles == null)
        {
            _logger.LogDebug("OpenSubtitles client not configured, skipping subtitle download");
            return subtitles;
        }

        try
        {
            // Try to search by file hash first (most accurate)
            var hashResults = await _openSubtitles.SearchByHashAsync(
                mediaFile.FilePath,
                string.Join(",", languages),
                ct);

            List<OpenSubtitlesResult> results;
            if (hashResults.Count > 0)
            {
                _logger.LogDebug("Found {Count} subtitles by hash for {File}",
                    hashResults.Count, mediaFile.FileName);
                results = hashResults;
            }
            else if (!string.IsNullOrEmpty(mediaFile.ExternalMetadata?.ImdbId))
            {
                // Try IMDB ID
                results = await _openSubtitles.SearchByImdbIdAsync(
                    mediaFile.ExternalMetadata.ImdbId,
                    string.Join(",", languages),
                    ct);
                _logger.LogDebug("Found {Count} subtitles by IMDB ID for {File}",
                    results.Count, mediaFile.FileName);
            }
            else if (!string.IsNullOrEmpty(mediaFile.ParsedTitle))
            {
                // Fallback to title search
                results = await _openSubtitles.SearchByQueryAsync(
                    mediaFile.ParsedTitle,
                    mediaFile.ParsedYear,
                    string.Join(",", languages),
                    ct);
                _logger.LogDebug("Found {Count} subtitles by title for {File}",
                    results.Count, mediaFile.FileName);
            }
            else
            {
                _logger.LogWarning("Cannot search subtitles: no hash, IMDB ID, or title for {File}",
                    mediaFile.FileName);
                return subtitles;
            }

            // Download best subtitle for each language
            foreach (var lang in languages)
            {
                var langResults = results
                    .Where(r => r.Attributes?.Language == lang)
                    .ToList();

                var best = _openSubtitles.FindBestMatch(langResults, lang);
                if (best?.Attributes?.Files?.FirstOrDefault() is { } file)
                {
                    var outputDir = Path.GetDirectoryName(mediaFile.FilePath) ?? Path.GetTempPath();
                    var baseName = Path.GetFileNameWithoutExtension(mediaFile.FilePath);
                    var outputPath = Path.Combine(outputDir, $"{baseName}.{lang}.srt");

                    var downloadedPath = await _openSubtitles.DownloadSubtitleAsync(
                        file.FileId,
                        outputPath,
                        ct);

                    if (downloadedPath != null)
                        subtitles.Add(new SubtitleFile
                        {
                            Id = Guid.NewGuid(),
                            FilePath = downloadedPath,
                            Language = best.Attributes.Language ?? lang,
                            LanguageCode = lang,
                            Source = "OpenSubtitles",
                            OpenSubtitlesId = best.Attributes.SubtitleId,
                            HearingImpaired = best.Attributes.HearingImpaired,
                            MachineTranslated = best.Attributes.MachineTranslated,
                            DownloadCount = best.Attributes.DownloadCount,
                            Fps = best.Attributes.Fps,
                            Status = SubtitleStatus.Downloaded
                        });
                }
            }

            _logger.LogInformation("Downloaded {Count} subtitles for {File}",
                subtitles.Count, mediaFile.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download subtitles for {File}", mediaFile.FileName);
        }

        return subtitles;
    }
}
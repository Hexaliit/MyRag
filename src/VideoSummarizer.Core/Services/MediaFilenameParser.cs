using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services;

/// <summary>
/// Parses movie/TV show information from filenames.
/// Handles common naming conventions from various release groups.
/// </summary>
public partial class MediaFilenameParser
{
    private readonly ILogger<MediaFilenameParser> _logger;

    public MediaFilenameParser(ILogger<MediaFilenameParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parse media information from a filename.
    /// </summary>
    public ParsedMediaInfo Parse(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var result = new ParsedMediaInfo { OriginalFilename = fileName };

        // Remove website prefixes like "[ www.TorrentDay.com ] - "
        fileName = WebsitePrefixRegex().Replace(fileName, "");

        // Try TV show pattern first (S01E01 or 1x01)
        var tvMatch = TvShowRegex().Match(fileName);
        if (tvMatch.Success)
        {
            result.MediaType = MediaType.TVShow;
            result.Title = CleanTitle(tvMatch.Groups["title"].Value);
            result.SeasonNumber = int.Parse(tvMatch.Groups["season"].Value);
            result.EpisodeNumber = int.Parse(tvMatch.Groups["episode"].Value);

            // Extract year if present before episode info
            var yearMatch = YearInTitleRegex().Match(tvMatch.Groups["title"].Value);
            if (yearMatch.Success)
            {
                result.Year = int.Parse(yearMatch.Groups["year"].Value);
                result.Title = CleanTitle(tvMatch.Groups["title"].Value.Replace(yearMatch.Value, ""));
            }

            ParseQualityInfo(fileName, result);
            return result;
        }

        // Try movie pattern with year
        var movieMatch = MovieWithYearRegex().Match(fileName);
        if (movieMatch.Success)
        {
            result.MediaType = MediaType.Movie;
            result.Title = CleanTitle(movieMatch.Groups["title"].Value);
            result.Year = int.Parse(movieMatch.Groups["year"].Value);
            ParseQualityInfo(fileName, result);
            return result;
        }

        // Try pattern with year in brackets [1976]
        var bracketYearMatch = BracketYearRegex().Match(fileName);
        if (bracketYearMatch.Success)
        {
            result.MediaType = MediaType.Movie;
            result.Year = int.Parse(bracketYearMatch.Groups["year"].Value);
            result.Title = CleanTitle(bracketYearMatch.Groups["title"].Value);
            ParseQualityInfo(fileName, result);
            return result;
        }

        // Home video pattern (dates like 2013-07-21 or 2013_07_21) - check BEFORE generic year
        var homeVideoMatch = HomeVideoDateRegex().Match(fileName);
        if (homeVideoMatch.Success)
        {
            result.MediaType = MediaType.HomeVideo;
            result.Year = int.Parse(homeVideoMatch.Groups["year"].Value);

            if (int.TryParse(homeVideoMatch.Groups["month"].Value, out var month) &&
                int.TryParse(homeVideoMatch.Groups["day"].Value, out var day))
            {
                try
                {
                    result.Date = new DateOnly(result.Year.Value, month, day);
                }
                catch
                {
                    // Invalid date, ignore
                }
            }

            // Use any text after the date as title
            var remaining = fileName.Substring(homeVideoMatch.Index + homeVideoMatch.Length).Trim();
            result.Title = string.IsNullOrWhiteSpace(remaining)
                ? $"Home Video {result.Date?.ToString("yyyy-MM-dd") ?? result.Year.ToString()}"
                : CleanTitle(remaining);

            return result;
        }

        // Fallback: try to find any year in the filename
        var anyYearMatch = AnyYearRegex().Match(fileName);
        if (anyYearMatch.Success)
        {
            result.Year = int.Parse(anyYearMatch.Groups["year"].Value);
            var titlePart = fileName.Substring(0, anyYearMatch.Index);
            result.Title = CleanTitle(titlePart);
            result.MediaType = MediaType.Movie;
            ParseQualityInfo(fileName, result);
            return result;
        }

        // Last resort: just clean up the filename
        result.MediaType = MediaType.Unknown;
        result.Title = CleanTitle(fileName);
        ParseQualityInfo(fileName, result);

        _logger.LogDebug("Parsed filename '{Filename}' as: {Title} ({Year})", filePath, result.Title, result.Year);
        return result;
    }

    private static void ParseQualityInfo(string fileName, ParsedMediaInfo result)
    {
        // Resolution
        if (ResolutionRegex().Match(fileName) is { Success: true } resMatch)
        {
            result.Resolution = resMatch.Value.ToUpperInvariant();
        }

        // Source
        var lowerName = fileName.ToLowerInvariant();
        if (lowerName.Contains("bluray") || lowerName.Contains("blu-ray") || lowerName.Contains("bdrip") || lowerName.Contains("brrip"))
            result.Source = "BluRay";
        else if (lowerName.Contains("webrip") || lowerName.Contains("web-dl") || lowerName.Contains("webdl"))
            result.Source = "WEB";
        else if (lowerName.Contains("hdtv"))
            result.Source = "HDTV";
        else if (lowerName.Contains("dvdrip") || lowerName.Contains("dvdscr"))
            result.Source = "DVD";
        else if (lowerName.Contains("hdcam") || lowerName.Contains("cam"))
            result.Source = "CAM";

        // Codec
        if (lowerName.Contains("x264") || lowerName.Contains("h264") || lowerName.Contains("h.264"))
            result.VideoCodec = "H.264";
        else if (lowerName.Contains("x265") || lowerName.Contains("h265") || lowerName.Contains("h.265") || lowerName.Contains("hevc"))
            result.VideoCodec = "H.265";
        else if (lowerName.Contains("xvid"))
            result.VideoCodec = "XviD";
        else if (lowerName.Contains("divx"))
            result.VideoCodec = "DivX";

        // Audio codec
        if (lowerName.Contains("dts"))
            result.AudioCodec = "DTS";
        else if (lowerName.Contains("ac3") || lowerName.Contains("dolby"))
            result.AudioCodec = "AC3";
        else if (lowerName.Contains("aac"))
            result.AudioCodec = "AAC";
        else if (lowerName.Contains("mp3"))
            result.AudioCodec = "MP3";

        // Release group
        var groupMatch = ReleaseGroupRegex().Match(fileName);
        if (groupMatch.Success)
        {
            result.ReleaseGroup = groupMatch.Groups["group"].Value;
        }
    }

    private static string CleanTitle(string title)
    {
        // Replace common separators with spaces
        title = title.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

        // Remove quality indicators that might be in the title part
        title = QualityWordsRegex().Replace(title, " ");

        // Collapse multiple spaces
        title = MultipleSpacesRegex().Replace(title, " ");

        // Trim
        return title.Trim();
    }

    // Compiled regex patterns for performance
    [GeneratedRegex(@"^\[\s*www\.[^\]]+\]\s*-?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex WebsitePrefixRegex();

    [GeneratedRegex(@"(?<title>.+?)[.\s_-]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,2})|(?<title>.+?)[.\s_-]+(?<season>\d{1,2})x(?<episode>\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex TvShowRegex();

    [GeneratedRegex(@"(?<title>.+?)[.\s_-]+\(?(?<year>(?:19|20)\d{2})\)?", RegexOptions.IgnoreCase)]
    private static partial Regex MovieWithYearRegex();

    [GeneratedRegex(@"^\[(?<year>(?:19|20)\d{2})\](?<title>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BracketYearRegex();

    [GeneratedRegex(@"\((?<year>(?:19|20)\d{2})\)", RegexOptions.IgnoreCase)]
    private static partial Regex YearInTitleRegex();

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AnyYearRegex();

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})[._-]?(?<month>\d{2})[._-]?(?<day>\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex HomeVideoDateRegex();

    [GeneratedRegex(@"(?:2160|1080|720|480|4k)[pi]?", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]+)(?:\.[a-z]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseGroupRegex();

    [GeneratedRegex(@"\b(1080p|720p|480p|2160p|4k|bluray|bdrip|brrip|webrip|web-dl|hdtv|dvdrip|x264|x265|h264|h265|hevc|xvid|aac|ac3|dts|ganool|yify|rarbg|ettv)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityWordsRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpacesRegex();
}

/// <summary>
/// Parsed information from a media filename.
/// </summary>
public class ParsedMediaInfo
{
    public string? OriginalFilename { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public DateOnly? Date { get; set; }
    public MediaType MediaType { get; set; } = MediaType.Unknown;

    // TV Show specific
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    // Quality info
    public string? Resolution { get; set; }
    public string? Source { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? ReleaseGroup { get; set; }

    /// <summary>
    /// Confidence in the parse (0-1)
    /// </summary>
    public double Confidence =>
        (Title != null ? 0.3 : 0) +
        (Year.HasValue ? 0.3 : 0) +
        (MediaType != MediaType.Unknown ? 0.2 : 0) +
        (Resolution != null ? 0.1 : 0) +
        (Source != null ? 0.1 : 0);
}

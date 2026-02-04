namespace VideoSummarizer.Core.Models;

/// <summary>
///     Represents a media file in the library with enriched metadata.
/// </summary>
public record MediaFile
{
    public required Guid Id { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public required DateTimeOffset LastModified { get; init; }

    /// <summary>Parsed title from filename or metadata</summary>
    public string? ParsedTitle { get; set; }

    /// <summary>Parsed year from filename or metadata</summary>
    public int? ParsedYear { get; set; }

    /// <summary>Content hash for deduplication</summary>
    public string? ContentHash { get; set; }

    /// <summary>External metadata from TMDB/OMDB/etc.</summary>
    public ExternalMediaMetadata? ExternalMetadata { get; set; }

    /// <summary>Downloaded subtitles</summary>
    public List<SubtitleFile> Subtitles { get; set; } = [];

    /// <summary>Processing status</summary>
    public MediaFileStatus Status { get; set; } = MediaFileStatus.Discovered;
}

public enum MediaFileStatus
{
    Discovered,
    Parsing,
    FetchingMetadata,
    DownloadingSubtitles,
    Processing,
    Completed,
    Failed
}

/// <summary>
///     External metadata from TMDB, OMDB, or other sources.
/// </summary>
public record ExternalMediaMetadata
{
    /// <summary>Source of the metadata (TMDB, OMDB, etc.)</summary>
    public required string Source { get; init; }

    /// <summary>External ID (TMDB ID, IMDB ID, etc.)</summary>
    public required string ExternalId { get; init; }

    /// <summary>Official title</summary>
    public required string Title { get; init; }

    /// <summary>Original title (if different from localized)</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Plot summary / overview</summary>
    public string? Overview { get; init; }

    /// <summary>Release year</summary>
    public int? Year { get; init; }

    /// <summary>Release date</summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>Runtime in minutes</summary>
    public int? RuntimeMinutes { get; init; }

    /// <summary>Genres</summary>
    public List<string> Genres { get; init; } = [];

    /// <summary>Directors</summary>
    public List<string> Directors { get; init; } = [];

    /// <summary>Writers</summary>
    public List<string> Writers { get; init; } = [];

    /// <summary>Cast members</summary>
    public List<CastMember> Cast { get; init; } = [];

    /// <summary>IMDB rating (0-10)</summary>
    public double? ImdbRating { get; init; }

    /// <summary>IMDB votes</summary>
    public int? ImdbVotes { get; init; }

    /// <summary>TMDB rating (0-10)</summary>
    public double? TmdbRating { get; init; }

    /// <summary>Rotten Tomatoes score (0-100)</summary>
    public int? RottenTomatoesScore { get; init; }

    /// <summary>Poster URL</summary>
    public string? PosterUrl { get; init; }

    /// <summary>Backdrop URL</summary>
    public string? BackdropUrl { get; init; }

    /// <summary>Tagline</summary>
    public string? Tagline { get; init; }

    /// <summary>Production countries</summary>
    public List<string> Countries { get; init; } = [];

    /// <summary>Spoken languages</summary>
    public List<string> Languages { get; init; } = [];

    /// <summary>IMDB ID (tt1234567)</summary>
    public string? ImdbId { get; init; }

    /// <summary>Content rating (PG-13, R, etc.)</summary>
    public string? ContentRating { get; init; }

    /// <summary>Budget in USD</summary>
    public long? Budget { get; init; }

    /// <summary>Revenue in USD</summary>
    public long? Revenue { get; init; }

    /// <summary>Keywords/tags</summary>
    public List<string> Keywords { get; init; } = [];

    /// <summary>For TV shows: season number</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>For TV shows: episode number</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>For TV shows: episode title</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>For TV shows: series name</summary>
    public string? SeriesName { get; init; }
}

/// <summary>
///     Cast member with optional face embedding for recognition.
/// </summary>
public record CastMember
{
    public required string Name { get; init; }
    public string? Character { get; init; }
    public string? ProfileUrl { get; init; }
    public int? Order { get; init; }

    /// <summary>Face embedding for recognition (if available)</summary>
    public float[]? FaceEmbedding { get; set; }
}

/// <summary>
///     Downloaded subtitle file.
/// </summary>
public record SubtitleFile
{
    public required Guid Id { get; init; }
    public required string FilePath { get; init; }
    public required string Language { get; init; }
    public required string LanguageCode { get; init; }

    /// <summary>Source of the subtitle (OpenSubtitles, embedded, local)</summary>
    public required string Source { get; init; }

    /// <summary>OpenSubtitles ID if downloaded</summary>
    public string? OpenSubtitlesId { get; init; }

    /// <summary>Whether this is for hearing impaired</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Whether this is machine translated</summary>
    public bool MachineTranslated { get; init; }

    /// <summary>Download count (popularity indicator)</summary>
    public int? DownloadCount { get; init; }

    /// <summary>FPS the subtitle was created for (for sync)</summary>
    public double? Fps { get; init; }

    /// <summary>Processing status</summary>
    public SubtitleStatus Status { get; init; } = SubtitleStatus.Downloaded;
}

public enum SubtitleStatus
{
    Downloaded,
    Processing,
    Indexed,
    Failed
}

/// <summary>
///     Represents a media library (folder) configuration.
/// </summary>
public record MediaLibrary
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string RootPath { get; init; }

    /// <summary>Type of media in this library</summary>
    public MediaType MediaType { get; init; } = MediaType.Movie;

    /// <summary>Whether to recursively scan subdirectories</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>File extensions to include</summary>
    public List<string> Extensions { get; init; } = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm"];

    /// <summary>Patterns to exclude (e.g., "sample", "trailer")</summary>
    public List<string> ExcludePatterns { get; init; } = ["sample", "trailer", "extras"];

    /// <summary>Whether to auto-download subtitles</summary>
    public bool AutoDownloadSubtitles { get; init; } = true;

    /// <summary>Preferred subtitle languages (ISO 639-1 codes)</summary>
    public List<string> SubtitleLanguages { get; init; } = ["en"];

    /// <summary>Last scan time</summary>
    public DateTimeOffset? LastScanned { get; init; }
}

public enum MediaType
{
    Movie,
    TVShow,
    HomeVideo,
    Music,
    Unknown
}

/// <summary>
///     Result from media library scan.
/// </summary>
public record LibraryScanResult
{
    public required Guid LibraryId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }

    public int TotalFiles { get; init; }
    public int NewFiles { get; init; }
    public int UpdatedFiles { get; init; }
    public int RemovedFiles { get; init; }
    public int FailedFiles { get; init; }

    public List<string> Errors { get; init; } = [];
}
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Services;

/// <summary>
/// Scans media library directories and discovers video files.
/// </summary>
public class MediaLibraryScanner
{
    private readonly MediaFilenameParser _filenameParser;
    private readonly ILogger<MediaLibraryScanner> _logger;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpeg", ".mpg", ".m2ts", ".ts"
    };

    public MediaLibraryScanner(
        MediaFilenameParser filenameParser,
        ILogger<MediaLibraryScanner> logger)
    {
        _filenameParser = filenameParser;
        _logger = logger;
    }

    /// <summary>
    /// Scan a media library and discover all video files.
    /// </summary>
    public async Task<LibraryScanResult> ScanAsync(
        MediaLibrary library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var result = new LibraryScanResult
        {
            LibraryId = library.Id,
            StartTime = startTime,
            EndTime = startTime
        };

        var errors = new List<string>();

        try
        {
            _logger.LogInformation("Starting scan of library: {LibraryName} at {Path}",
                library.Name, library.RootPath);

            if (!Directory.Exists(library.RootPath))
            {
                var error = $"Library path does not exist: {library.RootPath}";
                _logger.LogError("{Error}", error);
                errors.Add(error);
                result = result with
                {
                    EndTime = DateTimeOffset.UtcNow,
                    Errors = errors
                };
                return result;
            }

            // Get all video files
            var extensions = library.Extensions.Count > 0
                ? library.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : VideoExtensions;

            var searchOption = library.Recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var allFiles = Directory.EnumerateFiles(library.RootPath, "*.*", searchOption)
                .Where(f => extensions.Contains(Path.GetExtension(f)))
                .ToList();

            _logger.LogInformation("Found {Count} video files in {Path}", allFiles.Count, library.RootPath);

            // Filter out excluded patterns
            if (library.ExcludePatterns.Count > 0)
            {
                allFiles = allFiles
                    .Where(f =>
                    {
                        var fileName = Path.GetFileName(f).ToLowerInvariant();
                        return !library.ExcludePatterns.Any(p =>
                            fileName.Contains(p, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                _logger.LogInformation("{Count} files after exclusion filter", allFiles.Count);
            }

            var mediaFiles = new List<MediaFile>();
            var processedCount = 0;

            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var mediaFile = await ProcessFileAsync(filePath, library, ct);
                    if (mediaFile != null)
                    {
                        mediaFiles.Add(mediaFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process file: {FilePath}", filePath);
                    errors.Add($"Failed to process: {filePath} - {ex.Message}");
                }

                processedCount++;
                if (processedCount % 100 == 0 || processedCount == allFiles.Count)
                {
                    progress?.Report(new ScanProgress(
                        processedCount,
                        allFiles.Count,
                        $"Scanned {processedCount}/{allFiles.Count} files"));
                }
            }

            result = result with
            {
                EndTime = DateTimeOffset.UtcNow,
                TotalFiles = allFiles.Count,
                NewFiles = mediaFiles.Count,
                FailedFiles = errors.Count,
                Errors = errors
            };

            _logger.LogInformation("Scan completed: {Total} files, {New} new, {Failed} failed",
                result.TotalFiles, result.NewFiles, result.FailedFiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed for library: {LibraryName}", library.Name);
            errors.Add($"Scan failed: {ex.Message}");
            result = result with
            {
                EndTime = DateTimeOffset.UtcNow,
                Errors = errors
            };
        }

        return result;
    }

    /// <summary>
    /// Process a single file and create MediaFile record.
    /// </summary>
    private async Task<MediaFile?> ProcessFileAsync(
        string filePath,
        MediaLibrary library,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        // Parse filename to extract title, year, etc.
        var parsed = _filenameParser.Parse(filePath);

        // Override media type if library specifies it
        var mediaType = library.MediaType != MediaType.Unknown
            ? library.MediaType
            : parsed.MediaType;

        var mediaFile = new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            ParsedTitle = parsed.Title,
            ParsedYear = parsed.Year,
            Status = MediaFileStatus.Discovered
        };

        return await Task.FromResult(mediaFile);
    }

    /// <summary>
    /// Check if a file looks like a video file based on extension.
    /// </summary>
    public static bool IsVideoFile(string filePath)
    {
        return VideoExtensions.Contains(Path.GetExtension(filePath));
    }

    /// <summary>
    /// Get all supported video extensions.
    /// </summary>
    public static IReadOnlySet<string> SupportedExtensions => VideoExtensions;
}

public record ScanProgress(int Processed, int Total, string Message)
{
    public double PercentComplete => Total > 0 ? (double)Processed / Total * 100 : 0;
}

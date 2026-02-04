using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Wave that enriches video metadata from external sources (TMDB, OMDB).
///     Runs early in the pipeline to inform other waves (e.g., face recognition can use cast info).
/// </summary>
public class ExternalMetadataWave : IVideoWave
{
    private readonly MediaFilenameParser _filenameParser;
    private readonly ILogger<ExternalMetadataWave> _logger;
    private readonly MediaMetadataService _metadataService;

    public ExternalMetadataWave(
        MediaMetadataService metadataService,
        MediaFilenameParser filenameParser,
        ILogger<ExternalMetadataWave> logger)
    {
        _metadataService = metadataService;
        _filenameParser = filenameParser;
        _logger = logger;
    }

    public string Name => "ExternalMetadata";
    public int Priority => 990; // Very early, after normalize
    public IReadOnlyList<string> Tags => [VideoSignalTags.Metadata];

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("Enriching video with external metadata: {Path}", context.VideoPath);

        // Parse filename to extract title/year
        var parsed = _filenameParser.Parse(context.VideoPath);

        if (string.IsNullOrEmpty(parsed.Title))
        {
            _logger.LogWarning("Could not parse title from filename: {Path}", context.VideoPath);
            return;
        }

        // Create a MediaFile for the enrichment service
        var mediaFile = new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = context.VideoPath,
            FileName = Path.GetFileName(context.VideoPath),
            FileSize = new FileInfo(context.VideoPath).Length,
            LastModified = File.GetLastWriteTimeUtc(context.VideoPath),
            ParsedTitle = parsed.Title,
            ParsedYear = parsed.Year
        };

        // Fetch external metadata
        mediaFile = await _metadataService.EnrichMetadataAsync(mediaFile, ct);

        if (mediaFile.ExternalMetadata != null)
        {
            // Store in context for other waves to use
            context.SetCached("external_metadata", mediaFile.ExternalMetadata);
            context.SetCached("parsed_filename", parsed);

            _logger.LogInformation("Enriched with metadata: {Title} ({Year}) - {Source}",
                mediaFile.ExternalMetadata.Title,
                mediaFile.ExternalMetadata.Year,
                mediaFile.ExternalMetadata.Source);

            // Store cast for face matching
            if (mediaFile.ExternalMetadata.Cast.Count > 0)
            {
                context.SetCached("cast_members", mediaFile.ExternalMetadata.Cast);
                _logger.LogDebug("Found {Count} cast members", mediaFile.ExternalMetadata.Cast.Count);
            }
        }
        else
        {
            _logger.LogDebug("No external metadata found for: {Title} ({Year})",
                parsed.Title, parsed.Year);
        }

        context.OnProgress?.Invoke("ExternalMetadata", 100);
    }
}
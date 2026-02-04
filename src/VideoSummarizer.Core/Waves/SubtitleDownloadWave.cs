using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Wave that downloads subtitles from OpenSubtitles and processes them.
///     Creates utterances that can supplement or replace audio transcription.
/// </summary>
public class SubtitleDownloadWave : IVideoWave
{
    private readonly ILogger<SubtitleDownloadWave> _logger;
    private readonly MediaMetadataService _metadataService;
    private readonly SubtitleProcessingService _subtitleProcessor;

    public SubtitleDownloadWave(
        MediaMetadataService metadataService,
        SubtitleProcessingService subtitleProcessor,
        ILogger<SubtitleDownloadWave> logger)
    {
        _metadataService = metadataService;
        _subtitleProcessor = subtitleProcessor;
        _logger = logger;
    }

    /// <summary>
    ///     Languages to download subtitles for (ISO 639-1 codes).
    /// </summary>
    public List<string> Languages { get; set; } = ["en"];

    public string Name => "SubtitleDownload";
    public int Priority => 600; // Before transcription (500)
    public IReadOnlyList<string> Tags => [VideoSignalTags.Speech, "subtitles"];

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("Downloading subtitles for: {Path}", context.VideoPath);

        // Get external metadata from previous wave
        var externalMetadata = context.GetCached<ExternalMediaMetadata>("external_metadata");
        var parsedFilename = context.GetCached<ParsedMediaInfo>("parsed_filename");

        // Create MediaFile for subtitle search
        var mediaFile = new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = context.VideoPath,
            FileName = Path.GetFileName(context.VideoPath),
            FileSize = new FileInfo(context.VideoPath).Length,
            LastModified = File.GetLastWriteTimeUtc(context.VideoPath),
            ParsedTitle = parsedFilename?.Title,
            ParsedYear = parsedFilename?.Year,
            ExternalMetadata = externalMetadata
        };

        context.OnProgress?.Invoke("SubtitleDownload", 10);

        // Download subtitles
        var subtitles = await _metadataService.DownloadSubtitlesAsync(
            mediaFile,
            Languages,
            ct);

        if (subtitles.Count == 0)
        {
            _logger.LogDebug("No subtitles found for: {Path}", context.VideoPath);
            context.OnProgress?.Invoke("SubtitleDownload", 100);
            return;
        }

        context.OnProgress?.Invoke("SubtitleDownload", 50);

        // Process each subtitle file
        var allUtterances = new List<Utterance>();
        var videoId = context.Metadata?.Id ?? Guid.NewGuid();

        foreach (var subtitle in subtitles)
            try
            {
                // Parse SRT file
                var entries = await _subtitleProcessor.ParseSrtAsync(subtitle.FilePath, ct);

                if (entries.Count == 0)
                {
                    _logger.LogWarning("No entries parsed from subtitle: {Path}", subtitle.FilePath);
                    continue;
                }

                // Convert to utterances
                var utterances = _subtitleProcessor.ToUtterances(entries, videoId, subtitle.Language);
                allUtterances.AddRange(utterances);

                // Store subtitle entries for later use (e.g., for content chunks)
                context.SetCached($"subtitle_entries_{subtitle.LanguageCode}", entries);
                context.SetCached($"subtitle_file_{subtitle.LanguageCode}", subtitle);

                _logger.LogInformation("Processed {Count} subtitle entries from {Language}",
                    entries.Count, subtitle.Language);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process subtitle: {Path}", subtitle.FilePath);
            }

        // Store subtitle-derived utterances
        if (allUtterances.Count > 0)
        {
            context.SetCached("subtitle_utterances", allUtterances);

            // Also add to main utterances list (can be merged with transcription later)
            foreach (var utterance in allUtterances) context.Utterances.Add(utterance);

            _logger.LogInformation("Added {Count} utterances from subtitles", allUtterances.Count);
        }

        // Store subtitle file references
        context.SetCached("downloaded_subtitles", subtitles);

        context.OnProgress?.Invoke("SubtitleDownload", 100);
    }
}
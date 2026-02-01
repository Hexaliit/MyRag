using DoomSummarizer.Models;
using DoomSummarizer.Plugins;

namespace DoomSummarizer.Sources.YouTube;

/// <summary>
/// YouTube source plugin for DoomSummarizer.
/// Detects YouTube URLs and extracts video metadata + subtitles without downloading video.
/// Uses description timestamps and subtitle gaps as chapter markers.
/// </summary>
public sealed class YouTubeSourcePlugin : ISourcePlugin
{
    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "youtube",
        Keys = ["youtube", "yt"],
        DisplayName = "YouTube",
        Description = "Extract video metadata and subtitles from YouTube. Uses description timestamps and subtitle gaps as chapter markers.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.NoAuth,
        PackageId = "Mostlylucid.LucidRAG.Plugins.YouTube",
        Examples = ["-s youtube:VIDEO_ID", "crawl https://youtube.com/watch?v=..."]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        // The source key may be "youtube:VIDEO_ID" or a full URL
        var input = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : context.RawPrompt ?? context.Query ?? "";

        // Try to resolve as a URL first, then as a video ID
        string url;
        if (YouTubeExtractor.IsYouTubeUrl(input))
        {
            url = input;
        }
        else
        {
            // Treat as a video ID
            url = $"https://www.youtube.com/watch?v={input}";
        }

        var extractor = new YouTubeExtractor();
        var result = await extractor.ExtractAsync(url, context.Progress, ct);
        return result.Items;
    }
}

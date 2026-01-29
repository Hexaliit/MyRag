using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Services;

namespace DoomSummarizer.Sources.HackerNews;

/// <summary>
/// Standalone HackerNews source plugin for DoomSummarizer.
/// Fetches top, best, new, ask, show, and job stories from the HN Firebase API.
/// </summary>
public sealed class HackerNewsSourcePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "hn",
        Keys = ["hn", "hackernews"],
        DisplayName = "Hacker News",
        Description = "Top, best, new, ask, show, and job stories from Hacker News.",
        Capabilities = SourceCapabilities.TopicBrowse | SourceCapabilities.NoAuth,
        PackageId = "Mostlylucid.DoomSummarizer.Source.HackerNews",
        Examples = ["-s hn"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        var config = context.Config?.Sources.HackerNews ?? new HackerNewsConfig();
        var fetcher = new HackerNewsFetcher(_httpClient);
        return await fetcher.FetchAsync(config, context.Limit, context.Progress);
    }
}

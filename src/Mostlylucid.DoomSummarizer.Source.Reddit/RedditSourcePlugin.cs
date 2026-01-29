using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Services;

namespace DoomSummarizer.Sources.Reddit;

/// <summary>
/// Standalone Reddit source plugin for DoomSummarizer.
/// Fetches posts from configured or specified subreddits.
/// </summary>
public sealed class RedditSourcePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "reddit",
        Keys = ["reddit"],
        DisplayName = "Reddit",
        Description = "Reddit posts from configured or specified subreddits.",
        Capabilities = SourceCapabilities.TopicBrowse | SourceCapabilities.SubSource | SourceCapabilities.NoAuth,
        PackageId = "Mostlylucid.DoomSummarizer.Source.Reddit",
        Scopes = ["programming", "csharp", "dotnet", "technology"],
        Examples = ["-s reddit", "-s reddit:dotnet"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        var redditConfig = context.Config?.Sources.Reddit ?? new RedditConfig();

        // Override subreddit if specified as sub-parameter (e.g. "reddit:csharp")
        if (context.SubParams.Count > 0)
        {
            redditConfig = redditConfig with { Subreddits = [context.SubParams[0]] };
        }

        var fetcher = new RedditFetcher(_httpClient);
        return await fetcher.FetchAsync(redditConfig, context.Limit, context.Progress);
    }
}

using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
///     Adapts <see cref="RedditFetcher" /> to the <see cref="ISourcePlugin" /> contract.
///     Handles "reddit" and "reddit:subreddit" patterns.
/// </summary>
public sealed class RedditPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "reddit",
        Keys = ["reddit"],
        DisplayName = "Reddit",
        Description = "Reddit posts from configured or specified subreddits.",
        Capabilities = SourceCapabilities.TopicBrowse | SourceCapabilities.SubSource | SourceCapabilities.NoAuth,
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
        if (context.SubParams.Count > 0) redditConfig = redditConfig with { Subreddits = [context.SubParams[0]] };

        var fetcher = new RedditFetcher(_httpClient);
        return await fetcher.FetchAsync(redditConfig, context.Limit, context.Progress);
    }
}
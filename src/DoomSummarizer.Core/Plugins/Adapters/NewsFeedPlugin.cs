using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
///     Adapts <see cref="NewsFetcher" /> to the <see cref="ISourcePlugin" /> contract.
///     Handles all RSS-based news sources: bbc, guardian, ars, verge, etc.
/// </summary>
public sealed class NewsFeedPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "news",
        Keys = NewsFetcher.KnownSources.Concat(["news"]).ToList(),
        DisplayName = "News Feeds",
        Description = "RSS-based news feeds from BBC, Guardian, Ars Technica, Verge, Wired, TechCrunch, and more.",
        Capabilities = SourceCapabilities.Feed | SourceCapabilities.SubSource | SourceCapabilities.NoAuth |
                       SourceCapabilities.NewsOnly,
        Scopes = NewsFetcher.KnownSources.ToList(),
        Examples = ["-s bbc", "-s guardian", "-s ars", "-s bbc:science"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        var newsFetcher = new NewsFetcher(_httpClient);
        var sourceName = context.SourceKey;
        var query = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : null;

        return await newsFetcher.FetchSourceAsync(sourceName, context.Limit, query);
    }
}
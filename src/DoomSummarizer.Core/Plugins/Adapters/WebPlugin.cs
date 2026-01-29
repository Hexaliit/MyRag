using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
/// Adapts <see cref="WebsiteFetcher"/> and <see cref="FeedDiscovery"/> to the <see cref="ISourcePlugin"/> contract.
/// Handles raw HTTP URLs with RSS feed discovery fallback.
/// </summary>
public sealed class WebPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "web",
        Keys = ["web"],
        DisplayName = "Web / RSS",
        Description = "Fetch any URL: discovers RSS feeds, falls back to HTML scraping.",
        Capabilities = SourceCapabilities.Feed | SourceCapabilities.NoAuth,
        Examples = ["-s https://example.com/feed"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        // The raw source is the URL itself
        var url = context.RawSource;

        // Try RSS feed discovery first
        var feedDiscovery = new FeedDiscovery(_httpClient);
        var (feedItems, _) = await feedDiscovery.FetchWithDiscoveryAsync(url, context.Limit);

        if (feedItems.Count > 0)
            return feedItems;

        // Fall back to HTML scraping
        await using var webFetcher = new WebsiteFetcher(_httpClient);
        return await webFetcher.FetchAsync([new WebsiteConfig { Url = url }]);
    }
}

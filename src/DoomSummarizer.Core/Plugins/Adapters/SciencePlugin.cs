using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
/// Adapts <see cref="SpaceflightNewsFetcher"/> and <see cref="UsgsEarthquakeFetcher"/> to the <see cref="ISourcePlugin"/> contract.
/// </summary>
public sealed class SciencePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "spaceflight",
        Keys = ["spaceflight", "space", "earthquake", "quake"],
        DisplayName = "Science Data",
        Description = "Spaceflight News API and USGS earthquake data.",
        Capabilities = SourceCapabilities.Feed | SourceCapabilities.NoAuth,
        Examples = ["-s spaceflight", "-s space", "-s earthquake", "-s earthquake:significant_month"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        return context.SourceKey switch
        {
            "spaceflight" or "space" => await new SpaceflightNewsFetcher(_httpClient).FetchAsync(context.Limit),
            "earthquake" or "quake" => await FetchEarthquakeAsync(context),
            _ => []
        };
    }

    private async Task<List<ContentItem>> FetchEarthquakeAsync(SourceFetchContext context)
    {
        var feed = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await new UsgsEarthquakeFetcher(_httpClient).FetchAsync(context.Limit, feed);
    }
}

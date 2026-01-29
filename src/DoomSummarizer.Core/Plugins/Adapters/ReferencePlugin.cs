using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
/// Adapts <see cref="WikipediaFetcher"/> and <see cref="FactCheckFetcher"/> to the <see cref="ISourcePlugin"/> contract.
/// </summary>
public sealed class ReferencePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "wiki",
        Keys = ["wikipedia", "wiki", "factcheck"],
        DisplayName = "Reference",
        Description = "Wikipedia current events and fact-checking sites.",
        Capabilities = SourceCapabilities.Feed | SourceCapabilities.SubSource | SourceCapabilities.NoAuth,
        Examples = ["-s wiki", "-s wiki:news", "-s factcheck", "-s factcheck:snopes"]
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
            "wikipedia" or "wiki" => await FetchWikipediaAsync(context),
            "factcheck" => await FetchFactCheckAsync(context),
            _ => []
        };
    }

    private async Task<List<ContentItem>> FetchWikipediaAsync(SourceFetchContext context)
    {
        var section = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await new WikipediaFetcher(_httpClient).FetchAsync(context.Limit, section);
    }

    private async Task<List<ContentItem>> FetchFactCheckAsync(SourceFetchContext context)
    {
        var site = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await new FactCheckFetcher(_httpClient).FetchAsync(context.Limit, site);
    }
}

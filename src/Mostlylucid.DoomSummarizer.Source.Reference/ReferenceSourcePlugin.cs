using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Services;

namespace DoomSummarizer.Sources.Reference;

/// <summary>
///     Standalone reference source plugin for DoomSummarizer.
///     Fetches Wikipedia current events and fact-checking content.
/// </summary>
public sealed class ReferenceSourcePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "wiki",
        Keys = ["wikipedia", "wiki", "factcheck"],
        DisplayName = "Reference",
        Description = "Wikipedia current events and fact-checking sites.",
        Capabilities = SourceCapabilities.Feed | SourceCapabilities.SubSource | SourceCapabilities.NoAuth,
        PackageId = "Mostlylucid.DoomSummarizer.Source.Reference",
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
        var fetcher = new WikipediaFetcher(_httpClient);
        var firstParam = context.SubParams.Count > 0 ? context.SubParams[0] : null;

        // If a sub-param looks like a section name, use it as section; otherwise treat as search query.
        // Also use the main Query if no sub-params are provided.
        var isSectionName = firstParam is "news" or "history" or "featured";

        if (isSectionName)
            return await fetcher.FetchAsync(context.Limit, firstParam);

        // Use sub-param as query, or fall back to the main query
        var query = !isSectionName && firstParam != null ? firstParam : context.Query;
        return await fetcher.FetchAsync(context.Limit, query: query);
    }

    private async Task<List<ContentItem>> FetchFactCheckAsync(SourceFetchContext context)
    {
        var site = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await new FactCheckFetcher(_httpClient).FetchAsync(context.Limit, site);
    }
}
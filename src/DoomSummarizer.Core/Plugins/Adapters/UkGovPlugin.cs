using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
///     Adapts UK Government data fetchers to the <see cref="ISourcePlugin" /> contract.
///     Routes to Parliament (Hansard), Police Crime Data, and Environment Agency Flood Monitoring APIs.
///     All free, no authentication required.
/// </summary>
public sealed class UkGovPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "parliament",
        Keys = ["parliament", "hansard", "ukpolice", "ukflood", "ukgov"],
        DisplayName = "UK Government",
        Description = "UK Parliament Hansard debates, Police crime data, and Environment Agency flood monitoring.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.Feed | SourceCapabilities.SubSource |
                       SourceCapabilities.NoAuth,
        Examples =
        [
            "-s parliament",
            "-s parliament:debates",
            "-s parliament:divisions",
            "-s ukpolice",
            "-s ukpolice:crime:51.5074,-0.1278",
            "-s ukpolice:forces",
            "-s ukflood",
            "-s ukflood:warnings",
            "-s ukflood:levels",
            "-s ukflood:rainfall"
        ]
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
            "parliament" or "hansard" => await FetchParliamentAsync(context),
            "ukpolice" => await FetchPoliceAsync(context),
            "ukflood" => await FetchFloodAsync(context),
            "ukgov" => await FetchAllUkGovAsync(context),
            _ => []
        };
    }

    private async Task<List<ContentItem>> FetchParliamentAsync(SourceFetchContext context)
    {
        var fetcher = new UkParliamentFetcher(_httpClient);
        var section = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await fetcher.FetchAsync(context.Limit, context.Query, section);
    }

    private async Task<List<ContentItem>> FetchPoliceAsync(SourceFetchContext context)
    {
        var fetcher = new UkPoliceFetcher(_httpClient);
        var section = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        // For crime and outcomes, pass remaining sub-params (coordinates, etc.)
        var remainingParams = context.SubParams.Count > 1
            ? context.SubParams.Skip(1).ToList()
            : null;
        return await fetcher.FetchAsync(context.Limit, section, remainingParams);
    }

    private async Task<List<ContentItem>> FetchFloodAsync(SourceFetchContext context)
    {
        var fetcher = new UkEnvironmentFetcher(_httpClient);
        var section = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await fetcher.FetchAsync(context.Limit, section, context.Query);
    }

    /// <summary>
    ///     Fetch from all UK Gov sources (used when -s ukgov is specified).
    ///     Returns a combined set focused on the most operationally relevant data.
    /// </summary>
    private async Task<List<ContentItem>> FetchAllUkGovAsync(SourceFetchContext context)
    {
        var perSource = Math.Max(3, context.Limit / 3);
        var tasks = new[]
        {
            new UkParliamentFetcher(_httpClient).FetchAsync(perSource, context.Query),
            new UkPoliceFetcher(_httpClient).FetchCrimeSummaryAsync(perSource),
            new UkEnvironmentFetcher(_httpClient).FetchFloodWarningsAsync(perSource)
        };

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(r => r)
            .Take(context.Limit)
            .ToList();
    }
}
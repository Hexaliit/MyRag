using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Resilience;
using ApiBudgetService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteApiBudgetService;
using CircuitBreakerService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteCircuitBreakerService;

namespace DoomSummarizer.Sources.Google;

/// <summary>
///     Standalone Google News and Places source plugin for DoomSummarizer.
///     Handles "gnews", "gnews_topic:TOPIC", "gsearch", and "gplaces" patterns.
/// </summary>
public sealed class GoogleSourcePlugin : ISourcePlugin
{
    private IApiBudget _apiBudget = null!;
    private ApiKeyService _apiKeys = null!;
    private ICircuitBreaker _circuitBreaker = null!;
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "gnews",
        Keys = ["gnews", "gnews_topic", "gsearch", "gplaces"],
        DisplayName = "Google News & Places",
        Description = "Google News RSS search, topic feeds, Google Search, and Google Places.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.TopicBrowse | SourceCapabilities.RequiresAuth |
                       SourceCapabilities.NoAuth | SourceCapabilities.NewsOnly,
        PackageId = "Mostlylucid.DoomSummarizer.Source.Google",
        RequiredApiKeys = ["google_search"],
        Examples =
        [
            "-s \"gnews:AI safety\"", "-s gnews_topic:HEALTH", "-s \"gsearch:query\"", "-s \"gplaces:restaurant\""
        ]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        _apiKeys = services.ApiKeys;
        _apiBudget = services.ApiBudget;
        _circuitBreaker = services.CircuitBreaker;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        return context.SourceKey switch
        {
            "gnews_topic" => await FetchGnewsTopicAsync(context),
            "gnews" => await FetchGnewsAsync(context),
            "gsearch" => await FetchGoogleSearchAsync(context, ct),
            "gplaces" => await FetchGooglePlacesAsync(context, ct),
            _ => []
        };
    }

    private async Task<List<ContentItem>> FetchGnewsTopicAsync(SourceFetchContext context)
    {
        var gnews = new GoogleNewsFetcher(_httpClient);
        var topic = context.SubParams.Count > 0 ? context.SubParams[0] : "WORLD";
        return await gnews.FetchTopicAsync(topic, context.Limit);
    }

    private async Task<List<ContentItem>> FetchGnewsAsync(SourceFetchContext context)
    {
        var gnews = new GoogleNewsFetcher(_httpClient);
        var query = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : context.RawPrompt ?? "";
        return await gnews.SearchAsync(query, context.Limit, 7);
    }

    private async Task<List<ContentItem>> FetchGoogleSearchAsync(SourceFetchContext context, CancellationToken ct)
    {
        var query = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : context.RawPrompt ?? context.Query ?? "";
        return await new GoogleSearchService(_httpClient, _apiKeys, (ApiBudgetService)_apiBudget,
                (CircuitBreakerService)_circuitBreaker)
            .SearchAsync(query, context.Limit * 2, context.Progress, ct);
    }

    private async Task<List<ContentItem>> FetchGooglePlacesAsync(SourceFetchContext context, CancellationToken ct)
    {
        var query = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : context.RawPrompt ?? context.Query ?? "";
        return await new GooglePlacesService(_httpClient, _apiKeys, (ApiBudgetService)_apiBudget,
                (CircuitBreakerService)_circuitBreaker)
            .SearchAsync(query, context.Limit, context.Progress, ct);
    }
}
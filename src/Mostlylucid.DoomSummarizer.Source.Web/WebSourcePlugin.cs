using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Resilience;
using ApiBudgetService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteApiBudgetService;
using CircuitBreakerService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteCircuitBreakerService;

namespace DoomSummarizer.Sources.Web;

/// <summary>
/// Standalone web search and scraping source plugin for DoomSummarizer.
/// Rotates across Brave, Serper, Tavily, Jina, DuckDuckGo, Google, and news APIs.
/// </summary>
public sealed class WebSourcePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;
    private ApiKeyService _apiKeys = null!;
    private ApiBudgetService _apiBudget = null!;
    private CircuitBreakerService _circuitBreaker = null!;
    private static int _searchApiRotation;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "search",
        Keys =
        [
            "search", "brave", "brave_search", "bravenews", "brave_news",
            "serper", "serpernews", "serper_news",
            "tavily", "jina", "ddg", "duckduckgo",
            "newsapi", "news_api", "newsdata", "news_data", "currents",
            "web"
        ],
        DisplayName = "Web Search & News APIs",
        Description = "Rotated web search across Brave, Serper, Tavily, Jina, DuckDuckGo, Google, and news APIs.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.RequiresAuth | SourceCapabilities.NoAuth,
        PackageId = "Mostlylucid.DoomSummarizer.Source.Web",
        RequiredApiKeys = ["brave_search", "serper", "tavily", "jina", "google_search", "newsapi", "newsdata", "currents"],
        Examples =
        [
            "-s \"search:query\"", "-s brave", "-s serper", "-s tavily", "-s jina", "-s ddg",
            "-s newsapi", "-s newsdata", "-s currents"
        ]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        _apiKeys = services.ApiKeys;
        _apiBudget = (ApiBudgetService)services.ApiBudget;
        _circuitBreaker = (CircuitBreakerService)services.CircuitBreaker;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        var query = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : context.RawPrompt ?? context.Query ?? "";

        var qualifiedQuery = QualifySearchQuery(query, context.Vibe);
        var limit = context.Limit;

        return context.SourceKey switch
        {
            "search" => await RotatedSearchAsync(qualifiedQuery, limit * 2, ct),
            "brave" or "brave_search" => await new BraveSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(qualifiedQuery, limit * 2, newsOnly: false, context.Progress, ct),
            "bravenews" or "brave_news" => await new BraveSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, newsOnly: true, context.Progress, ct),
            "serper" => await new SerperSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(qualifiedQuery, limit * 2, newsOnly: false, context.Progress, ct),
            "serpernews" or "serper_news" => await new SerperSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, newsOnly: true, context.Progress, ct),
            "tavily" => await new TavilySearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, progress: context.Progress, ct: ct),
            "jina" => await new JinaSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, context.Progress, ct),
            "ddg" or "duckduckgo" => await new DuckDuckGoSearch(_httpClient, _circuitBreaker)
                .SearchAsync(query, limit, context.Progress),
            "newsapi" or "news_api" => await new NewsApiService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, progress: context.Progress, ct: ct),
            "newsdata" or "news_data" => await new NewsDataService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, progress: context.Progress, ct: ct),
            "currents" => await new CurrentsApiService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit, progress: context.Progress, ct: ct),
            "web" => await FetchWebAsync(context, ct),
            _ => []
        };
    }

    private async Task<List<ContentItem>> FetchWebAsync(SourceFetchContext context, CancellationToken ct)
    {
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

    private async Task<List<ContentItem>> RotatedSearchAsync(string query, int limit, CancellationToken ct)
    {
        var available = new List<(string name, Func<Task<List<ContentItem>>> search)>();

        if (_apiKeys.HasGoogleSearch)
            available.Add(("google_search", () => new GoogleSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit)));
        if (_apiKeys.IsAvailable("brave_search"))
            available.Add(("brave_search", () => new BraveSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit)));
        if (_apiKeys.IsAvailable("serper"))
            available.Add(("serper", () => new SerperSearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit)));
        if (_apiKeys.IsAvailable("tavily"))
            available.Add(("tavily", () => new TavilySearchService(_httpClient, _apiKeys, _apiBudget, _circuitBreaker)
                .SearchAsync(query, limit)));

        // DDG always available as last resort
        available.Add(("duckduckgo", () => new DuckDuckGoSearch(_httpClient, _circuitBreaker)
            .SearchAsync(query, limit)));

        if (available.Count <= 1)
            return await available[0].search();

        // Rotate: pick starting index, try each in order, return first success
        var startIdx = Interlocked.Increment(ref _searchApiRotation) % (available.Count - 1);
        var paidServices = available.Count - 1;

        for (var i = 0; i < paidServices; i++)
        {
            var idx = (startIdx + i) % paidServices;
            var (name, search) = available[idx];

            if (ApiRateLimiter.IsCircuitOpen(name))
                continue;

            try
            {
                var results = await search();
                if (results.Count > 0)
                    return results;
            }
            catch
            {
                // Try next service
            }
        }

        // Fall back to DDG
        return await available[^1].search();
    }

    private static string QualifySearchQuery(string query, string vibe)
    {
        if (string.IsNullOrWhiteSpace(query) || vibe is "neutral" or "")
            return query;

        return vibe switch
        {
            "doom" => $"{query} problems risks concerns",
            "hopeful" => $"{query} positive progress breakthrough",
            "snarky" => query,
            "funny" => query,
            _ => query
        };
    }
}

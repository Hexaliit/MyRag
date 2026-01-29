using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
/// Adapts <see cref="GoogleNewsFetcher"/> to the <see cref="ISourcePlugin"/> contract.
/// Handles "gnews", "gnews:query", and "gnews_topic:TOPIC" patterns.
/// </summary>
public sealed class GoogleNewsPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "gnews",
        Keys = ["gnews", "gnews_topic"],
        DisplayName = "Google News",
        Description = "Google News RSS search and topic feeds.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.TopicBrowse | SourceCapabilities.NoAuth | SourceCapabilities.NewsOnly,
        Examples = ["-s \"gnews:AI safety\"", "-s gnews_topic:HEALTH"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
    {
        var gnews = new GoogleNewsFetcher(_httpClient);

        if (context.SourceKey == "gnews_topic")
        {
            // Topic feed: gnews_topic:HEALTH
            var topic = context.SubParams.Count > 0 ? context.SubParams[0] : "WORLD";
            return await gnews.FetchTopicAsync(topic, context.Limit);
        }

        // Search: gnews:query or bare gnews
        var query = context.SubParams.Count > 0
            ? string.Join(":", context.SubParams)
            : context.RawPrompt ?? "";
        return await gnews.SearchAsync(query, context.Limit, daysBack: 7);
    }
}

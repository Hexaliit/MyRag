using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
///     Adapts <see cref="ArxivFetcher" /> and <see cref="StackOverflowFetcher" /> to the <see cref="ISourcePlugin" />
///     contract.
/// </summary>
public sealed class AcademicPlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "arxiv",
        Keys = ["arxiv", "so"],
        DisplayName = "Academic & Q&A",
        Description = "arXiv papers and StackOverflow questions.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.TopicBrowse | SourceCapabilities.SubSource |
                       SourceCapabilities.NoAuth,
        Examples =
        [
            "-s arxiv", "-s \"arxiv:machine learning\"", "-s arxiv:cat:cs.AI", "-s so", "-s so:csharp",
            "-s \"so:search:async await\""
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
            "arxiv" => await FetchArxivAsync(context),
            "so" => await FetchStackOverflowAsync(context),
            _ => []
        };
    }

    private async Task<List<ContentItem>> FetchArxivAsync(SourceFetchContext context)
    {
        var fetcher = new ArxivFetcher(_httpClient);

        if (context.SubParams.Count >= 2 && context.SubParams[0] == "cat")
            // Category browse: arxiv:cat:cs.AI
            return await fetcher.FetchCategoryAsync(context.SubParams[1], context.Limit);

        if (context.SubParams.Count >= 1)
        {
            // Search: arxiv:query terms
            var query = string.Join(":", context.SubParams);
            return await fetcher.SearchAsync(query, context.Limit);
        }

        // Default: search with prompt
        var defaultQuery = context.RawPrompt ?? "recent";
        return await fetcher.SearchAsync(defaultQuery, context.Limit);
    }

    private static async Task<List<ContentItem>> FetchStackOverflowAsync(SourceFetchContext context)
    {
        var fetcher = new StackOverflowFetcher();

        if (context.SubParams.Count == 0) return await fetcher.FetchHotAsync(context.Limit);

        if (context.SubParams.Count >= 2 && context.SubParams[0] == "search")
        {
            // Search: so:search:query
            var query = string.Join(":", context.SubParams.Skip(1));
            return await fetcher.SearchAsync(query, context.Limit);
        }

        // Tag: so:csharp
        return await fetcher.FetchByTagAsync(context.SubParams[0], context.Limit);
    }
}
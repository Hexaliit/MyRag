using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Search;

namespace Mostlylucid.DocSummarizer.FullText.Lucene;

/// <summary>
///     DI registration for Lucene.NET full-text search.
/// </summary>
public static class LuceneFullTextExtensions
{
    /// <summary>
    ///     Register Lucene.NET-based full-text search.
    /// </summary>
    public static IServiceCollection AddLuceneFullTextSearch(this IServiceCollection services, string indexPath)
    {
        services.AddSingleton<IFullTextSearch>(new LuceneFullTextSearch(indexPath));
        return services;
    }
}
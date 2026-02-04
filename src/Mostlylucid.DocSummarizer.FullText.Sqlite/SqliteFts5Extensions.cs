using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Search;

namespace Mostlylucid.DocSummarizer.FullText.Sqlite;

/// <summary>
///     DI registration for SQLite FTS5 full-text search.
/// </summary>
public static class SqliteFts5Extensions
{
    /// <summary>
    ///     Register SQLite FTS5-based full-text search.
    /// </summary>
    public static IServiceCollection AddSqliteFts5Search(this IServiceCollection services, string dbPath)
    {
        services.AddSingleton<IFullTextSearch>(new SqliteFts5SearchService(dbPath));
        return services;
    }
}
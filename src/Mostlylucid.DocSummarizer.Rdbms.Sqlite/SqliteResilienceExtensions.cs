using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Resilience;

namespace Mostlylucid.DocSummarizer.Rdbms.Sqlite;

/// <summary>
///     DI registration for SQLite-backed resilience services.
/// </summary>
public static class SqliteResilienceExtensions
{
    /// <summary>
    ///     Register SQLite-backed circuit breaker and API budget services.
    /// </summary>
    public static IServiceCollection AddSqliteResilience(this IServiceCollection services, string dbPath)
    {
        services.AddSingleton<ICircuitBreaker>(new SqliteCircuitBreakerService(dbPath));
        services.AddSingleton<IApiBudget>(sp => new SqliteApiBudgetService(
            sp.GetRequiredService<ApiBudgetConfig>(),
            sp.GetRequiredService<IServiceBudgetLookup>(),
            dbPath));
        return services;
    }
}
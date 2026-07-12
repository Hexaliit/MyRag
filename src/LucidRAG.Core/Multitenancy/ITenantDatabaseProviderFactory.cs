using System.Threading;
using System.Threading.Tasks;

namespace LucidRAG.Multitenancy;

/// <summary>
///     Factory for resolving the appropriate tenant database provider based on configuration.
/// </summary>
public interface ITenantDatabaseProviderFactory
{
    /// <summary>
    ///     Gets the tenant database provider for the configured provider type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if provider is not supported.</exception>
    ITenantDatabaseProvider GetProvider();

    /// <summary>
    ///     Gets the tenant database provider for a specific provider name.
    /// </summary>
    /// <param name="providerName">Provider name (Postgres, Sqlite, SqlServer, Oracle).</param>
    /// <exception cref="InvalidOperationException">Thrown if provider is not supported.</exception>
    ITenantDatabaseProvider GetProvider(string providerName);
}
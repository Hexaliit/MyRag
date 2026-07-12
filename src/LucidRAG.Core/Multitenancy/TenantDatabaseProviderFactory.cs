using LucidRAG.Multitenancy.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LucidRAG.Multitenancy;

/// <summary>
///     Factory implementation for resolving tenant database providers.
/// </summary>
public sealed class TenantDatabaseProviderFactory : ITenantDatabaseProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TenantDatabaseOptions _options;
    private readonly ILogger<TenantDatabaseProviderFactory> _logger;

    public TenantDatabaseProviderFactory(
        IServiceProvider serviceProvider,
        IOptions<TenantDatabaseOptions> options,
        ILogger<TenantDatabaseProviderFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderName => _options.Provider;

    public ITenantDatabaseProvider GetProvider()
    {
        return GetProvider(_options.Provider);
    }

    public ITenantDatabaseProvider GetProvider(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new InvalidOperationException(
                "TenantDatabase:Provider is not configured. " +
                "Supported values: Postgres, Sqlite, SqlServer, Oracle");
        }

        _logger.LogInformation("Creating tenant database provider: {Provider}", providerName);

        ITenantDatabaseProvider provider = providerName.Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" => _serviceProvider.GetRequiredService<PostgresTenantDatabaseProvider>(),
            "sqlite" => _serviceProvider.GetRequiredService<SqliteTenantDatabaseProvider>(),
            "sqlserver" => _serviceProvider.GetRequiredService<SqlServerTenantDatabaseProvider>(),
            "oracle" => _serviceProvider.GetRequiredService<OracleTenantDatabaseProvider>(),
            _ => null
        };

        if (provider == null)
        {
            var supported = new[] { "Postgres", "Sqlite", "SqlServer", "Oracle" };
            var message = $"Unsupported tenant database provider: '{providerName}'. " +
                          $"Supported providers: {string.Join(", ", supported)}";
            _logger.LogError(message);
            throw new InvalidOperationException(message);
        }

        _logger.LogInformation("Resolved tenant database provider: {Provider}", provider.ProviderName);
        return provider;
    }
}
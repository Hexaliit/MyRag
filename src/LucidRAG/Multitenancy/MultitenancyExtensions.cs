using LucidRAG.Multitenancy.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LucidRAG.Multitenancy;

/// <summary>
///     Extension methods for configuring multi-tenancy.
/// </summary>
public static class MultitenancyExtensions
{
    /// <summary>
    ///     Add multi-tenancy services to the service collection.
    /// </summary>
    public static IServiceCollection AddMultitenancy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configuration
        services.Configure<MultitenancyOptions>(
            configuration.GetSection(MultitenancyOptions.SectionName));

        services.Configure<TenantDatabaseOptions>(
            configuration.GetSection(TenantDatabaseOptions.SectionName));

        // Memory cache for tenant resolution
        services.AddMemoryCache();

        // Tenant accessor (scoped - one per request)
        services.AddScoped<ITenantAccessor, TenantAccessor>();

        // Tenant resolver
        services.AddScoped<ITenantResolver, SubdomainTenantResolver>();

        // Tenant management DbContext (for tenant metadata table)
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<TenantDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Tenants", "public");
            }));

        // Tenant DbContext factory for provisioning
        services.AddScoped<ITenantDbContextFactory, PostgresTenantDbContextFactory>();

        // Register tenant database providers
        services.AddScoped<PostgresTenantDatabaseProvider>();
        services.AddScoped<SqliteTenantDatabaseProvider>();
        services.AddScoped<SqlServerTenantDatabaseProvider>();
        services.AddScoped<OracleTenantDatabaseProvider>();

        // Factory for resolving the correct provider
        services.AddSingleton<ITenantDatabaseProviderFactory, TenantDatabaseProviderFactory>();

        // Scoped provider accessor - resolves the configured provider per scope
        services.AddScoped<ITenantDatabaseProvider>(sp =>
            sp.GetRequiredService<ITenantDatabaseProviderFactory>().GetProvider());

        // Provisioning service
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        
        return services;
    }

    /// <summary>
    ///     Ensure tenant management tables are created and seeded.
    ///     Uses the configured provider abstraction for database-agnostic operation.
    /// </summary>
    public static async Task EnsureTenantTablesAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDatabaseProviderFactory>();
        var provider = factory.GetProvider();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ITenantDatabaseProviderFactory>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TenantDatabaseOptions>>().Value;

        logger.LogInformation("Ensuring tenant tables for provider: {Provider}", provider.ProviderName);

        await provider.EnsureTenantTablesAsync();

        if (options.SeedOnStartup)
        {
            await provider.SeedAsync(options.DefaultTenantId, options.DefaultTenantName);
        }
    }
}
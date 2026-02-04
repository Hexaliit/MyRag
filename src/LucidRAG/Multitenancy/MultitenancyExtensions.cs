using Microsoft.EntityFrameworkCore;

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

        // Memory cache for tenant resolution
        services.AddMemoryCache();

        // Tenant accessor (scoped - one per request)
        services.AddScoped<ITenantAccessor, TenantAccessor>();

        // Tenant resolver
        services.AddScoped<ITenantResolver, SubdomainTenantResolver>();

        // Tenant DbContext (for tenant management table)
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<TenantDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Tenant-aware DbContext factory
        services.AddScoped<ITenantDbContextFactory, PostgresTenantDbContextFactory>();

        // Provisioning service
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        return services;
    }

    /// <summary>
    ///     Ensure tenant management tables are created/migrated.
    /// </summary>
    public static async Task EnsureTenantTablesAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TenantDbContext>>();

        // Check if tenants table already exists
        var conn = tenantDb.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'tenants'";
            var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

            if (tableExists)
            {
                logger.LogInformation("Tenant management tables already exist");
                return;
            }

            // Table doesn't exist, use EnsureCreated to create it
            logger.LogInformation("Creating tenant management tables...");
            await tenantDb.Database.EnsureCreatedAsync();
            logger.LogInformation("Tenant management tables created");
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
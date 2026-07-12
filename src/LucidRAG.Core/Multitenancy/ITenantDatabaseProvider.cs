using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LucidRAG.Multitenancy;

/// <summary>
///     Abstraction for tenant metadata database operations.
///     Providers implement provider-specific SQL for PostgreSQL, SQLite, SQL Server, Oracle.
/// </summary>
public interface ITenantDatabaseProvider
{
    /// <summary>
    ///     Gets the provider name (e.g., "Postgres", "Sqlite", "SqlServer", "Oracle").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Ensures the tenant metadata tables exist.
    ///     Creates tables if they don't exist.
    /// </summary>
    Task EnsureTenantTablesAsync(CancellationToken ct = default);

    /// <summary>
    ///     Seeds the default tenant if not already present.
    ///     Idempotent - safe to call multiple times.
    /// </summary>
    Task SeedAsync(string defaultTenantId, string defaultTenantName, CancellationToken ct = default);

    /// <summary>
    ///     Creates a new tenant record.
    /// </summary>
    Task<TenantRecord> CreateTenantAsync(TenantRecord tenant, CancellationToken ct = default);

    /// <summary>
    ///     Deletes a tenant record.
    /// </summary>
    Task DeleteTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Updates a tenant record.
    /// </summary>
    Task<TenantRecord> UpdateTenantAsync(TenantRecord tenant, CancellationToken ct = default);

    /// <summary>
    ///     Checks if a tenant exists.
    /// </summary>
    Task<bool> TenantExistsAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Gets a tenant by ID.
    /// </summary>
    Task<TenantRecord?> GetTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Gets all tenants, optionally filtered by active status.
    /// </summary>
    Task<IReadOnlyList<TenantRecord>> GetTenantsAsync(bool? isActive = null, CancellationToken ct = default);
}
using System.Data;
using System.Data.Common;
using System.Text;
using LucidRAG.Multitenancy;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LucidRAG.Multitenancy.Providers;

/// <summary>
///     Microsoft SQL Server implementation of tenant database provider.
///     Uses Microsoft.Data.SqlClient for SQL Server-specific features.
/// </summary>
public sealed class SqlServerTenantDatabaseProvider : ITenantDatabaseProvider
{
    private readonly TenantDatabaseOptions _options;
    private readonly ILogger<SqlServerTenantDatabaseProvider> _logger;

    public string ProviderName => "SqlServer";

    public SqlServerTenantDatabaseProvider(
        IOptions<TenantDatabaseOptions> options,
        ILogger<SqlServerTenantDatabaseProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private SqlConnection CreateConnection()
    {
        var conn = new SqlConnection(_options.ConnectionString);
        return conn;
    }

    public async Task EnsureTenantTablesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring tenant tables exist for SQL Server");

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Check if tenants table exists in dbo schema
        var checkTableSql = @"
            IF NOT EXISTS (
                SELECT * FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'tenants'
            )
            BEGIN
                CREATE TABLE dbo.tenants (
                    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                    tenant_id NVARCHAR(64) NOT NULL UNIQUE,
                    schema_name NVARCHAR(128) NOT NULL UNIQUE,
                    collection_name NVARCHAR(128) NOT NULL,
                    display_name NVARCHAR(256) NULL,
                    contact_email NVARCHAR(256) NULL,
                    is_active BIT NOT NULL DEFAULT 1,
                    is_provisioned BIT NOT NULL DEFAULT 0,
                    settings NVARCHAR(MAX) NULL,
                    plan NVARCHAR(32) NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    provisioned_at DATETIME2 NULL,
                    last_accessed_at DATETIME2 NULL
                );

                CREATE INDEX IX_tenants_tenant_id ON dbo.tenants (tenant_id);
                CREATE INDEX IX_tenants_is_active ON dbo.tenants (is_active);
                CREATE INDEX IX_tenants_schema_name ON dbo.tenants (schema_name);
            END";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = checkTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Tenant tables ensured for SQL Server");
    }

    public async Task SeedAsync(string defaultTenantId, string defaultTenantName, CancellationToken ct = default)
    {
        _logger.LogInformation("Seeding default tenant for SQL Server: {TenantId}", defaultTenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Check if default tenant already exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"SELECT COUNT(*) FROM dbo.tenants WHERE tenant_id = @tenantId";
        checkCmd.Parameters.Add(new SqlParameter("@tenantId", defaultTenantId));
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct));

        if (count > 0)
        {
            _logger.LogInformation("Default tenant already exists, skipping seed: {TenantId}", defaultTenantId);
            return;
        }

        // Insert default tenant
        var schemaName = $"tenant_{defaultTenantId}";
        var collectionName = $"tenant_{defaultTenantId}_vectors";

        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO dbo.tenants (
                id, tenant_id, schema_name, collection_name,
                display_name, is_active, is_provisioned, plan,
                created_at, updated_at
            ) VALUES (
                NEWID(), @tenantId, @schemaName, @collectionName,
                @displayName, @isActive, @isProvisioned, @plan,
                SYSUTCDATETIME(), SYSUTCDATETIME()
            )";

        insertCmd.Parameters.Add(new SqlParameter("@tenantId", defaultTenantId));
        insertCmd.Parameters.Add(new SqlParameter("@schemaName", schemaName));
        insertCmd.Parameters.Add(new SqlParameter("@collectionName", collectionName));
        insertCmd.Parameters.Add(new SqlParameter("@displayName", defaultTenantName));
        insertCmd.Parameters.Add(new SqlParameter("@isActive", 1));
        insertCmd.Parameters.Add(new SqlParameter("@isProvisioned", 0));
        insertCmd.Parameters.Add(new SqlParameter("@plan", TenantPlans.Free));

        await insertCmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Seeded default tenant: {TenantId}", defaultTenantId);
    }

    public async Task<TenantRecord> CreateTenantAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating tenant: {TenantId}", tenant.TenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        tenant.Id = Guid.NewGuid();
        tenant.CreatedAt = DateTimeOffset.UtcNow;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO dbo.tenants (
                id, tenant_id, schema_name, collection_name,
                display_name, contact_email, is_active, is_provisioned,
                settings, plan, created_at, updated_at
            ) VALUES (
                @id, @tenantId, @schemaName, @collectionName,
                @displayName, @contactEmail, @isActive, @isProvisioned,
                @settings, @plan, @createdAt, @updatedAt
            )";

        AddTenantParameters(cmd, tenant);

        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Created tenant: {TenantId}", tenant.TenantId);
        return tenant;
    }

    public async Task DeleteTenantAsync(string tenantId, CancellationToken ct = default)
    {
        _logger.LogInformation("Deleting tenant: {TenantId}", tenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM dbo.tenants WHERE tenant_id = @tenantId";
        cmd.Parameters.Add(new SqlParameter("@tenantId", tenantId));

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        if (rows == 0)
        {
            _logger.LogWarning("Tenant not found for deletion: {TenantId}", tenantId);
            throw new InvalidOperationException($"Tenant '{tenantId}' not found");
        }

        _logger.LogInformation("Deleted tenant: {TenantId}", tenantId);
    }

    public async Task<TenantRecord> UpdateTenantAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        _logger.LogInformation("Updating tenant: {TenantId}", tenant.TenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE dbo.tenants SET
                schema_name = @schemaName,
                collection_name = @collectionName,
                display_name = @displayName,
                contact_email = @contactEmail,
                is_active = @isActive,
                is_provisioned = @isProvisioned,
                settings = @settings,
                plan = @plan,
                updated_at = @updatedAt,
                provisioned_at = @provisionedAt,
                last_accessed_at = @lastAccessedAt
            WHERE tenant_id = @tenantId";

        AddTenantParameters(cmd, tenant);

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        if (rows == 0)
        {
            throw new InvalidOperationException($"Tenant '{tenant.TenantId}' not found");
        }

        _logger.LogInformation("Updated tenant: {TenantId}", tenant.TenantId);
        return tenant;
    }

    public async Task<bool> TenantExistsAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM dbo.tenants WHERE tenant_id = @tenantId";
        cmd.Parameters.Add(new SqlParameter("@tenantId", tenantId));

        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task<TenantRecord?> GetTenantAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, tenant_id, schema_name, collection_name,
                   display_name, contact_email, is_active, is_provisioned,
                   settings, plan, created_at, updated_at,
                   provisioned_at, last_accessed_at
            FROM dbo.tenants
            WHERE tenant_id = @tenantId";
        cmd.Parameters.Add(new SqlParameter("@tenantId", tenantId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadTenant(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<TenantRecord>> GetTenantsAsync(bool? isActive = null, CancellationToken ct = default)
    {
        var tenants = new List<TenantRecord>();

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sql = new StringBuilder(@"
            SELECT id, tenant_id, schema_name, collection_name,
                   display_name, contact_email, is_active, is_provisioned,
                   settings, plan, created_at, updated_at,
                   provisioned_at, last_accessed_at
            FROM dbo.tenants");

        if (isActive.HasValue)
        {
            sql.Append(" WHERE is_active = @isActive");
        }

        sql.Append(" ORDER BY tenant_id");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (isActive.HasValue)
        {
            cmd.Parameters.Add(new SqlParameter("@isActive", isActive.Value));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(ReadTenant(reader));
        }

        return tenants;
    }

    private static void AddTenantParameters(SqlCommand cmd, TenantRecord tenant)
    {
        cmd.Parameters.Add(new SqlParameter("@id", tenant.Id));
        cmd.Parameters.Add(new SqlParameter("@tenantId", tenant.TenantId));
        cmd.Parameters.Add(new SqlParameter("@schemaName", tenant.SchemaName));
        cmd.Parameters.Add(new SqlParameter("@collectionName", tenant.CollectionName));
        cmd.Parameters.Add(new SqlParameter("@displayName", tenant.DisplayName ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@contactEmail", tenant.ContactEmail ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@isActive", tenant.IsActive));
        cmd.Parameters.Add(new SqlParameter("@isProvisioned", tenant.IsProvisioned));
        cmd.Parameters.Add(new SqlParameter("@settings", tenant.Settings ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@plan", tenant.Plan ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@createdAt", tenant.CreatedAt.UtcDateTime));
        cmd.Parameters.Add(new SqlParameter("@updatedAt", tenant.UpdatedAt.UtcDateTime));
        cmd.Parameters.Add(new SqlParameter("@provisionedAt", tenant.ProvisionedAt?.UtcDateTime ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lastAccessedAt", tenant.LastAccessedAt?.UtcDateTime ?? (object)DBNull.Value));
    }

    private static TenantRecord ReadTenant(SqlDataReader reader)
    {
        return new TenantRecord
        {
            Id = reader.GetGuid(0),
            TenantId = reader.GetString(1),
            SchemaName = reader.GetString(2),
            CollectionName = reader.GetString(3),
            DisplayName = reader.IsDBNull(4) ? null : reader.GetString(4),
            ContactEmail = reader.IsDBNull(5) ? null : reader.GetString(5),
            IsActive = reader.GetBoolean(6),
            IsProvisioned = reader.GetBoolean(7),
            Settings = reader.IsDBNull(8) ? null : reader.GetString(8),
            Plan = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetDateTime(10).Ticks / 10000),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetDateTime(11).Ticks / 10000),
            ProvisionedAt = reader.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetDateTime(12).Ticks / 10000),
            LastAccessedAt = reader.IsDBNull(13) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetDateTime(13).Ticks / 10000)
        };
    }
}
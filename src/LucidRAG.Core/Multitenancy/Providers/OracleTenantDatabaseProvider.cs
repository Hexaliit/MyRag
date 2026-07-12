using System.Data;
using System.Data.Common;
using System.Text;
using LucidRAG.Multitenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace LucidRAG.Multitenancy.Providers;

/// <summary>
///     Oracle implementation of tenant database provider.
///     Uses Oracle.ManagedDataAccess.Core for Oracle-specific features.
/// </summary>
public sealed class OracleTenantDatabaseProvider : ITenantDatabaseProvider
{
    private readonly TenantDatabaseOptions _options;
    private readonly ILogger<OracleTenantDatabaseProvider> _logger;

    public string ProviderName => "Oracle";

    public OracleTenantDatabaseProvider(
        IOptions<TenantDatabaseOptions> options,
        ILogger<OracleTenantDatabaseProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private OracleConnection CreateConnection()
    {
        var conn = new OracleConnection(_options.ConnectionString);
        return conn;
    }

    public async Task EnsureTenantTablesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring tenant tables exist for Oracle");

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Check if tenants table exists in current user's schema
        // Use PL/SQL block with exception handling for idempotent creation
        var createTableSql = @"
            BEGIN
                EXECUTE IMMEDIATE '
                    CREATE TABLE tenants (
                        id RAW(16) DEFAULT SYS_GUID() PRIMARY KEY,
                        tenant_id VARCHAR2(64) NOT NULL UNIQUE,
                        schema_name VARCHAR2(128) NOT NULL UNIQUE,
                        collection_name VARCHAR2(128) NOT NULL,
                        display_name VARCHAR2(256),
                        contact_email VARCHAR2(256),
                        is_active NUMBER(1) NOT NULL DEFAULT 1,
                        is_provisioned NUMBER(1) NOT NULL DEFAULT 0,
                        settings CLOB,
                        plan VARCHAR2(32),
                        created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                        updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                        provisioned_at TIMESTAMP WITH TIME ZONE,
                        last_accessed_at TIMESTAMP WITH TIME ZONE
                    )';
                EXECUTE IMMEDIATE 'CREATE INDEX idx_tenants_tenant_id ON tenants (tenant_id)';
                EXECUTE IMMEDIATE 'CREATE INDEX idx_tenants_is_active ON tenants (is_active)';
                EXECUTE IMMEDIATE 'CREATE INDEX idx_tenants_schema_name ON tenants (schema_name)';
            EXCEPTION
                WHEN OTHERS THEN
                    IF SQLCODE != -955 THEN -- ORA-00955: name is already used by existing object
                        RAISE;
                    END IF;
            END;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = createTableSql;
        cmd.CommandType = CommandType.Text;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Tenant tables ensured for Oracle");
    }

    public async Task SeedAsync(string defaultTenantId, string defaultTenantName, CancellationToken ct = default)
    {
        _logger.LogInformation("Seeding default tenant for Oracle: {TenantId}", defaultTenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Check if default tenant already exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"SELECT COUNT(*) FROM tenants WHERE tenant_id = :tenantId";
        checkCmd.Parameters.Add(new OracleParameter("tenantId", defaultTenantId));
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
            INSERT INTO tenants (
                id, tenant_id, schema_name, collection_name,
                display_name, is_active, is_provisioned, plan,
                created_at, updated_at
            ) VALUES (
                SYS_GUID(), :tenantId, :schemaName, :collectionName,
                :displayName, :isActive, :isProvisioned, :plan,
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            )";

        insertCmd.Parameters.Add(new OracleParameter("tenantId", defaultTenantId));
        insertCmd.Parameters.Add(new OracleParameter("schemaName", schemaName));
        insertCmd.Parameters.Add(new OracleParameter("collectionName", collectionName));
        insertCmd.Parameters.Add(new OracleParameter("displayName", defaultTenantName));
        insertCmd.Parameters.Add(new OracleParameter("isActive", 1));
        insertCmd.Parameters.Add(new OracleParameter("isProvisioned", 0));
        insertCmd.Parameters.Add(new OracleParameter("plan", TenantPlans.Free));

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
            INSERT INTO tenants (
                id, tenant_id, schema_name, collection_name,
                display_name, contact_email, is_active, is_provisioned,
                settings, plan, created_at, updated_at
            ) VALUES (
                :id, :tenantId, :schemaName, :collectionName,
                :displayName, :contactEmail, :isActive, :isProvisioned,
                :settings, :plan, :createdAt, :updatedAt
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
        cmd.CommandText = @"DELETE FROM tenants WHERE tenant_id = :tenantId";
        cmd.Parameters.Add(new OracleParameter("tenantId", tenantId));

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
            UPDATE tenants SET
                schema_name = :schemaName,
                collection_name = :collectionName,
                display_name = :displayName,
                contact_email = :contactEmail,
                is_active = :isActive,
                is_provisioned = :isProvisioned,
                settings = :settings,
                plan = :plan,
                updated_at = :updatedAt,
                provisioned_at = :provisionedAt,
                last_accessed_at = :lastAccessedAt
            WHERE tenant_id = :tenantId";

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
        cmd.CommandText = @"SELECT COUNT(*) FROM tenants WHERE tenant_id = :tenantId";
        cmd.Parameters.Add(new OracleParameter("tenantId", tenantId));

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
            FROM tenants
            WHERE tenant_id = :tenantId";
        cmd.Parameters.Add(new OracleParameter("tenantId", tenantId));

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
            FROM tenants");

        if (isActive.HasValue)
        {
            sql.Append(" WHERE is_active = :isActive");
        }

        sql.Append(" ORDER BY tenant_id");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (isActive.HasValue)
        {
            cmd.Parameters.Add(new OracleParameter("isActive", isActive.Value ? 1 : 0));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(ReadTenant(reader));
        }

        return tenants;
    }

    private static void AddTenantParameters(OracleCommand cmd, TenantRecord tenant)
    {
        cmd.Parameters.Add(new OracleParameter("id", tenant.Id.ToByteArray()));
        cmd.Parameters.Add(new OracleParameter("tenantId", tenant.TenantId));
        cmd.Parameters.Add(new OracleParameter("schemaName", tenant.SchemaName));
        cmd.Parameters.Add(new OracleParameter("collectionName", tenant.CollectionName));
        cmd.Parameters.Add(new OracleParameter("displayName", tenant.DisplayName ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("contactEmail", tenant.ContactEmail ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("isActive", tenant.IsActive ? 1 : 0));
        cmd.Parameters.Add(new OracleParameter("isProvisioned", tenant.IsProvisioned ? 1 : 0));
        cmd.Parameters.Add(new OracleParameter("settings", tenant.Settings ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("plan", tenant.Plan ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("createdAt", tenant.CreatedAt.UtcDateTime));
        cmd.Parameters.Add(new OracleParameter("updatedAt", tenant.UpdatedAt.UtcDateTime));
        cmd.Parameters.Add(new OracleParameter("provisionedAt", tenant.ProvisionedAt?.UtcDateTime ?? (object)DBNull.Value));
        cmd.Parameters.Add(new OracleParameter("lastAccessedAt", tenant.LastAccessedAt?.UtcDateTime ?? (object)DBNull.Value));
    }

    private static TenantRecord ReadTenant(OracleDataReader reader)
    {
        var idBytes = new byte[16];
        reader.GetBytes(0, 0, idBytes, 0, 16);

        return new TenantRecord
        {
            Id = new Guid(idBytes),
            TenantId = reader.GetString(1),
            SchemaName = reader.GetString(2),
            CollectionName = reader.GetString(3),
            DisplayName = reader.IsDBNull(4) ? null : reader.GetString(4),
            ContactEmail = reader.IsDBNull(5) ? null : reader.GetString(5),
            IsActive = reader.GetInt32(6) == 1,
            IsProvisioned = reader.GetInt32(7) == 1,
            Settings = reader.IsDBNull(8) ? null : reader.GetString(8),
            Plan = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt = new DateTimeOffset(reader.GetDateTime(10)),
            UpdatedAt = new DateTimeOffset(reader.GetDateTime(11)),
            ProvisionedAt = reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12)),
            LastAccessedAt = reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetDateTime(13))
        };
    }
}
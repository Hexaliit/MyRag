using System.Data;
using System.Data.Common;
using System.Text;
using LucidRAG.Multitenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace LucidRAG.Multitenancy.Providers;

/// <summary>
///     SQLite implementation of tenant database provider.
///     Uses Microsoft.Data.Sqlite for SQLite-specific features.
/// </summary>
public sealed class SqliteTenantDatabaseProvider : ITenantDatabaseProvider
{
    private readonly TenantDatabaseOptions _options;
    private readonly ILogger<SqliteTenantDatabaseProvider> _logger;

    public string ProviderName => "Sqlite";

    public SqliteTenantDatabaseProvider(
        IOptions<TenantDatabaseOptions> options,
        ILogger<SqliteTenantDatabaseProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_options.ConnectionString);
        return conn;
    }

    public async Task EnsureTenantTablesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring tenant tables exist for SQLite");

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // SQLite uses CREATE TABLE IF NOT EXISTS
        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS tenants (
                id BLOB PRIMARY KEY,
                tenant_id TEXT(64) NOT NULL UNIQUE,
                schema_name TEXT(128) NOT NULL UNIQUE,
                collection_name TEXT(128) NOT NULL,
                display_name TEXT(256),
                contact_email TEXT(256),
                is_active INTEGER NOT NULL DEFAULT 1,
                is_provisioned INTEGER NOT NULL DEFAULT 0,
                settings TEXT,
                plan TEXT(32),
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                provisioned_at TEXT,
                last_accessed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_tenants_tenant_id ON tenants (tenant_id);
            CREATE INDEX IF NOT EXISTS idx_tenants_is_active ON tenants (is_active);
            CREATE INDEX IF NOT EXISTS idx_tenants_schema_name ON tenants (schema_name);";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = createTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Tenant tables ensured for SQLite");
    }

    public async Task SeedAsync(string defaultTenantId, string defaultTenantName, CancellationToken ct = default)
    {
        _logger.LogInformation("Seeding default tenant for SQLite: {TenantId}", defaultTenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Check if default tenant already exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"SELECT COUNT(*) FROM tenants WHERE tenant_id = @tenantId";
        checkCmd.Parameters.Add(new SqliteParameter("@tenantId", defaultTenantId));
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct));

        if (count > 0)
        {
            _logger.LogInformation("Default tenant already exists, skipping seed: {TenantId}", defaultTenantId);
            return;
        }

        // Insert default tenant
        var tenantId = Guid.NewGuid();
        var schemaName = $"tenant_{defaultTenantId}";
        var collectionName = $"tenant_{defaultTenantId}_vectors";
        var now = DateTimeOffset.UtcNow;

        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO tenants (
                id, tenant_id, schema_name, collection_name,
                display_name, is_active, is_provisioned, plan,
                created_at, updated_at
            ) VALUES (
                @id, @tenantId, @schemaName, @collectionName,
                @displayName, @isActive, @isProvisioned, @plan,
                @createdAt, @updatedAt
            )";

        insertCmd.Parameters.Add(new SqliteParameter("@id", tenantId.ToByteArray()));
        insertCmd.Parameters.Add(new SqliteParameter("@tenantId", defaultTenantId));
        insertCmd.Parameters.Add(new SqliteParameter("@schemaName", schemaName));
        insertCmd.Parameters.Add(new SqliteParameter("@collectionName", collectionName));
        insertCmd.Parameters.Add(new SqliteParameter("@displayName", defaultTenantName));
        insertCmd.Parameters.Add(new SqliteParameter("@isActive", 1));
        insertCmd.Parameters.Add(new SqliteParameter("@isProvisioned", 0));
        insertCmd.Parameters.Add(new SqliteParameter("@plan", TenantPlans.Free));
        insertCmd.Parameters.Add(new SqliteParameter("@createdAt", now.ToString("O")));
        insertCmd.Parameters.Add(new SqliteParameter("@updatedAt", now.ToString("O")));

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
        cmd.CommandText = @"DELETE FROM tenants WHERE tenant_id = @tenantId";
        cmd.Parameters.Add(new SqliteParameter("@tenantId", tenantId));

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
        cmd.CommandText = @"SELECT COUNT(*) FROM tenants WHERE tenant_id = @tenantId";
        cmd.Parameters.Add(new SqliteParameter("@tenantId", tenantId));

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
            WHERE tenant_id = @tenantId";
        cmd.Parameters.Add(new SqliteParameter("@tenantId", tenantId));

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
            sql.Append(" WHERE is_active = @isActive");
        }

        sql.Append(" ORDER BY tenant_id");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (isActive.HasValue)
        {
            cmd.Parameters.Add(new SqliteParameter("@isActive", isActive.Value ? 1 : 0));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(ReadTenant(reader));
        }

        return tenants;
    }

    private static void AddTenantParameters(SqliteCommand cmd, TenantRecord tenant)
    {
        cmd.Parameters.Add(new SqliteParameter("@id", tenant.Id.ToByteArray()));
        cmd.Parameters.Add(new SqliteParameter("@tenantId", tenant.TenantId));
        cmd.Parameters.Add(new SqliteParameter("@schemaName", tenant.SchemaName));
        cmd.Parameters.Add(new SqliteParameter("@collectionName", tenant.CollectionName));
        cmd.Parameters.Add(new SqliteParameter("@displayName", tenant.DisplayName ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@contactEmail", tenant.ContactEmail ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@isActive", tenant.IsActive ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@isProvisioned", tenant.IsProvisioned ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@settings", tenant.Settings ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@plan", tenant.Plan ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAt", tenant.CreatedAt.ToString("O")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAt", tenant.UpdatedAt.ToString("O")));
        cmd.Parameters.Add(new SqliteParameter("@provisionedAt", tenant.ProvisionedAt?.ToString("O") ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@lastAccessedAt", tenant.LastAccessedAt?.ToString("O") ?? (object)DBNull.Value));
    }

    private static TenantRecord ReadTenant(SqliteDataReader reader)
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
            CreatedAt = DateTimeOffset.Parse(reader.GetString(10)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(11)),
            ProvisionedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            LastAccessedAt = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13))
        };
    }
}
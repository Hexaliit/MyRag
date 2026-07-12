using LucidRAG.Multitenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text;

namespace LucidRAG.Multitenancy.Providers;

/// <summary>
///     PostgreSQL implementation of tenant database provider.
///     Uses Npgsql for PostgreSQL-specific features.
///     Schema matches EF Core conventions for TenantEntity compatibility.
/// </summary>
public sealed class PostgresTenantDatabaseProvider : ITenantDatabaseProvider
{
    private readonly TenantDatabaseOptions _options;
    private readonly ILogger<PostgresTenantDatabaseProvider> _logger;

    public string ProviderName => "Postgres";

    public PostgresTenantDatabaseProvider(
        IOptions<TenantDatabaseOptions> options,
        ILogger<PostgresTenantDatabaseProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection()
    {
        var conn = new NpgsqlConnection(_options.ConnectionString);
        return conn;
    }

    public async Task EnsureTenantTablesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring tenant tables exist for PostgreSQL");

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        // Create schema if not exists (for non-public schemas)
        if (!schemaName.Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            await using var schemaCmd = conn.CreateCommand();
            schemaCmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\"";
            await schemaCmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Ensured schema exists: {Schema}", schemaName);
        }

        // Create tenants table - column names match EF Core snake_case conventions
        var createTableSql = $@"
            CREATE TABLE IF NOT EXISTS ""{schemaName}"".""tenants"" (
                ""id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                ""tenant_id"" VARCHAR(64) NOT NULL UNIQUE,
                ""schema_name"" VARCHAR(128) NOT NULL UNIQUE,
                ""qdrant_collection"" VARCHAR(128) NOT NULL,
                ""display_name"" VARCHAR(256),
                ""contact_email"" VARCHAR(256),
                ""is_active"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""is_provisioned"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""settings"" JSONB,
                ""plan"" VARCHAR(32),
                ""created_at"" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                ""updated_at"" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                ""provisioned_at"" TIMESTAMPTZ,
                ""last_accessed_at"" TIMESTAMPTZ
            );

            CREATE INDEX IF NOT EXISTS ""ix_tenants_tenant_id"" ON ""{schemaName}"".""tenants"" (""tenant_id"");
            CREATE INDEX IF NOT EXISTS ""ix_tenants_is_active"" ON ""{schemaName}"".""tenants"" (""is_active"");
            CREATE INDEX IF NOT EXISTS ""ix_tenants_schema_name"" ON ""{schemaName}"".""tenants"" (""schema_name"");";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = createTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Tenant tables ensured for PostgreSQL schema: {Schema}", schemaName);
    }

    public async Task SeedAsync(string defaultTenantId, string defaultTenantName, CancellationToken ct = default)
    {
        _logger.LogInformation("Seeding default tenant for PostgreSQL: {TenantId}", defaultTenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        // Check if default tenant already exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $@"SELECT COUNT(*) FROM ""{schemaName}"".""tenants"" WHERE ""tenant_id"" = @tenantId";
        checkCmd.Parameters.Add(new NpgsqlParameter("@tenantId", defaultTenantId));
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct));

        if (count > 0)
        {
            _logger.LogInformation("Default tenant already exists, skipping seed: {TenantId}", defaultTenantId);
            return;
        }

        // Insert default tenant
        var tenantId = Guid.NewGuid();
        var schemaNameForTenant = $"tenant_{defaultTenantId}";
        var collectionName = $"tenant_{defaultTenantId}_vectors";
        var now = DateTimeOffset.UtcNow;

        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = $@"
            INSERT INTO ""{schemaName}"".""tenants"" (
                ""id"", ""tenant_id"", ""schema_name"", ""qdrant_collection"",
                ""display_name"", ""is_active"", ""is_provisioned"", ""plan"",
                ""created_at"", ""updated_at""
            ) VALUES (
                @id, @tenantId, @schemaName, @collectionName,
                @displayName, @isActive, @isProvisioned, @plan,
                @createdAt, @updatedAt
            )";

        insertCmd.Parameters.Add(new NpgsqlParameter("@id", tenantId));
        insertCmd.Parameters.Add(new NpgsqlParameter("@tenantId", defaultTenantId));
        insertCmd.Parameters.Add(new NpgsqlParameter("@schemaName", schemaNameForTenant));
        insertCmd.Parameters.Add(new NpgsqlParameter("@collectionName", collectionName));
        insertCmd.Parameters.Add(new NpgsqlParameter("@displayName", defaultTenantName));
        insertCmd.Parameters.Add(new NpgsqlParameter("@isActive", true));
        insertCmd.Parameters.Add(new NpgsqlParameter("@isProvisioned", false));
        insertCmd.Parameters.Add(new NpgsqlParameter("@plan", TenantPlans.Free));
        insertCmd.Parameters.Add(new NpgsqlParameter("@createdAt", now));
        insertCmd.Parameters.Add(new NpgsqlParameter("@updatedAt", now));

        await insertCmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Seeded default tenant: {TenantId}", defaultTenantId);
    }

    public async Task<TenantRecord> CreateTenantAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating tenant: {TenantId}", tenant.TenantId);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        tenant.Id = Guid.NewGuid();
        tenant.CreatedAt = DateTimeOffset.UtcNow;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO ""{schemaName}"".""tenants"" (
                ""id"", ""tenant_id"", ""schema_name"", ""qdrant_collection"",
                ""display_name"", ""contact_email"", ""is_active"", ""is_provisioned"",
                ""settings"", ""plan"", ""created_at"", ""updated_at""
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

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"DELETE FROM ""{schemaName}"".""tenants"" WHERE ""tenant_id"" = @tenantId";
        cmd.Parameters.Add(new NpgsqlParameter("@tenantId", tenantId));

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

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE ""{schemaName}"".""tenants"" SET
                ""schema_name"" = @schemaName,
                ""qdrant_collection"" = @collectionName,
                ""display_name"" = @displayName,
                ""contact_email"" = @contactEmail,
                ""is_active"" = @isActive,
                ""is_provisioned"" = @isProvisioned,
                ""settings"" = @settings,
                ""plan"" = @plan,
                ""updated_at"" = @updatedAt,
                ""provisioned_at"" = @provisionedAt,
                ""last_accessed_at"" = @lastAccessedAt
            WHERE ""tenant_id"" = @tenantId";

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

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""{schemaName}"".""tenants"" WHERE ""tenant_id"" = @tenantId";
        cmd.Parameters.Add(new NpgsqlParameter("@tenantId", tenantId));

        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task<TenantRecord?> GetTenantAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT ""id"", ""tenant_id"", ""schema_name"", ""qdrant_collection"",
                   ""display_name"", ""contact_email"", ""is_active"", ""is_provisioned"",
                   ""settings"", ""plan"", ""created_at"", ""updated_at"",
                   ""provisioned_at"", ""last_accessed_at""
            FROM ""{schemaName}"".""tenants""
            WHERE ""tenant_id"" = @tenantId";
        cmd.Parameters.Add(new NpgsqlParameter("@tenantId", tenantId));

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

        var schemaName = _options.SchemaName;
        TenantDatabaseExtensions.ValidateSchemaName(schemaName);

        var sql = new StringBuilder($@"
            SELECT ""id"", ""tenant_id"", ""schema_name"", ""qdrant_collection"",
                   ""display_name"", ""contact_email"", ""is_active"", ""is_provisioned"",
                   ""settings"", ""plan"", ""created_at"", ""updated_at"",
                   ""provisioned_at"", ""last_accessed_at""
            FROM ""{schemaName}"".""tenants""");

        if (isActive.HasValue)
        {
            sql.Append(" WHERE \"is_active\" = @isActive");
        }

        sql.Append(" ORDER BY \"tenant_id\"");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (isActive.HasValue)
        {
            cmd.Parameters.Add(new NpgsqlParameter("@isActive", isActive.Value));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(ReadTenant(reader));
        }

        return tenants;
    }

    private static void AddTenantParameters(NpgsqlCommand cmd, TenantRecord tenant)
    {
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant.Id));
        cmd.Parameters.Add(new NpgsqlParameter("@tenantId", tenant.TenantId));
        cmd.Parameters.Add(new NpgsqlParameter("@schemaName", tenant.SchemaName));
        cmd.Parameters.Add(new NpgsqlParameter("@collectionName", tenant.CollectionName));
        cmd.Parameters.Add(new NpgsqlParameter("@displayName", tenant.DisplayName ?? (object)DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@contactEmail", tenant.ContactEmail ?? (object)DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@isActive", tenant.IsActive));
        cmd.Parameters.Add(new NpgsqlParameter("@isProvisioned", tenant.IsProvisioned));
        cmd.Parameters.Add(new NpgsqlParameter("@settings", tenant.Settings ?? (object)DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@plan", tenant.Plan ?? (object)DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@createdAt", tenant.CreatedAt));
        cmd.Parameters.Add(new NpgsqlParameter("@updatedAt", tenant.UpdatedAt));
        cmd.Parameters.Add(new NpgsqlParameter("@provisionedAt", tenant.ProvisionedAt ?? (object)DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@lastAccessedAt", tenant.LastAccessedAt ?? (object)DBNull.Value));
    }

    private static TenantRecord ReadTenant(NpgsqlDataReader reader)
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
            CreatedAt = new DateTimeOffset(reader.GetDateTime(10)),
            UpdatedAt = new DateTimeOffset(reader.GetDateTime(11)),
            ProvisionedAt = reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12)),
            LastAccessedAt = reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetDateTime(13))
        };
    }
}
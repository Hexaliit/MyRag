namespace LucidRAG.Multitenancy;

/// <summary>
///     Configuration options for tenant database provider.
/// </summary>
public class TenantDatabaseOptions
{
    public const string SectionName = "TenantDatabase";

    /// <summary>
    ///     Database provider type.
    ///     Supported values: "Postgres", "Sqlite", "SqlServer", "Oracle"
    /// </summary>
    public string Provider { get; set; } = "Postgres";

    /// <summary>
    ///     Database connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    ///     Whether to automatically create tenant tables and seed data on startup.
    /// </summary>
    public bool SeedOnStartup { get; set; } = true;

    /// <summary>
    ///     Default tenant ID for seed data.
    /// </summary>
    public string DefaultTenantId { get; set; } = "default";

    /// <summary>
    ///     Default tenant display name for seed data.
    /// </summary>
    public string DefaultTenantName { get; set; } = "Default Tenant";

    /// <summary>
    ///     Schema name for tenant tables (PostgreSQL/Oracle only).
    /// </summary>
    public string SchemaName { get; set; } = "public";
}
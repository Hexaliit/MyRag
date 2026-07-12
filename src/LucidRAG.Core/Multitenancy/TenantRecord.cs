using System.ComponentModel.DataAnnotations;

namespace LucidRAG.Multitenancy;

/// <summary>
///     Database entity for tenant registration.
///     Stored in the shared tenant management schema/table.
/// </summary>
public class TenantRecord
{
    /// <summary>
    ///     Unique identifier for the tenant record.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     Unique tenant identifier (e.g., "acme").
    ///     Used in subdomain: acme.lucidrag.com
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string TenantId { get; set; }

    /// <summary>
    ///     Database schema name for this tenant.
    ///     Format: "tenant_{tenantId}" (PostgreSQL/Oracle)
    /// </summary>
    [Required]
    [MaxLength(128)]
    public required string SchemaName { get; set; }

    /// <summary>
    ///     Vector store collection name for this tenant.
    ///     Format: "tenant_{tenantId}_vectors"
    /// </summary>
    [Required]
    [MaxLength(128)]
    public required string CollectionName { get; set; }

    /// <summary>
    ///     Display name for the tenant.
    /// </summary>
    [MaxLength(256)]
    public string? DisplayName { get; set; }

    /// <summary>
    ///     Contact email for the tenant.
    /// </summary>
    [MaxLength(256)]
    public string? ContactEmail { get; set; }

    /// <summary>
    ///     Whether this tenant is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    ///     Whether the tenant schema has been provisioned.
    /// </summary>
    public bool IsProvisioned { get; set; } = false;

    /// <summary>
    ///     Tenant-specific settings (JSON).
    /// </summary>
    public string? Settings { get; set; }

    /// <summary>
    ///     Subscription tier or plan.
    /// </summary>
    [MaxLength(32)]
    public string? Plan { get; set; }

    /// <summary>
    ///     When this tenant was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     When this tenant was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     When this tenant was provisioned.
    /// </summary>
    public DateTimeOffset? ProvisionedAt { get; set; }

    /// <summary>
    ///     When this tenant was last accessed.
    /// </summary>
    public DateTimeOffset? LastAccessedAt { get; set; }
}
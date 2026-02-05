namespace LucidSupport.Models;

/// <summary>
///     Groups fields into a logical section for workflow visibility rules.
///     Maps to a ### [section-id] Label block in ## Sections.
/// </summary>
public sealed record Section
{
    /// <summary>Section identifier (e.g., "billing", "shipping").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label (e.g., "Billing Information").</summary>
    public required string Label { get; init; }

    /// <summary>CSS selectors of fields belonging to this section.</summary>
    public List<string> Fields { get; init; } = [];

    /// <summary>Display order (lower = first).</summary>
    public int Order { get; init; }
}

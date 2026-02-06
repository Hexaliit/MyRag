namespace LucidSupport.Models;

/// <summary>
///     Pattern-based guidance for a specific field.
/// </summary>
public sealed record FieldGuidance
{
    /// <summary>CSS selector of the target field.</summary>
    public required string Selector { get; init; }

    /// <summary>Pattern name (e.g., "email", "credit-card").</summary>
    public required string Pattern { get; init; }

    /// <summary>Human-readable format hint.</summary>
    public required string FormatHint { get; init; }

    /// <summary>Example value (if applicable).</summary>
    public string? Example { get; init; }

    /// <summary>Privacy note (e.g., "Never stored").</summary>
    public string? PrivacyNote { get; init; }

    /// <summary>Whether this guidance is proactive (no error present) vs reactive.</summary>
    public bool IsProactive { get; init; }
}

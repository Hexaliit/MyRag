namespace LucidSupport.Models;

/// <summary>
///     A single interactive field extracted from a page.
///     Maps to a ### [#selector] Label section in .support.md.
/// </summary>
public sealed record FieldDefinition
{
    /// <summary>CSS selector for the field (e.g., "#card-number").</summary>
    public required string Selector { get; init; }

    /// <summary>Human-readable label for the field.</summary>
    public required string Label { get; init; }

    /// <summary>HTML input type (text, email, password, select, textarea, etc.).</summary>
    public string? Type { get; init; }

    /// <summary>The label text as displayed in the UI (from label element or aria-label).</summary>
    public string? DisplayLabel { get; init; }

    /// <summary>Placeholder text from the element.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Named data pattern (credit-card, email, phone, etc.) - never actual regex.</summary>
    public string? Pattern { get; init; }

    /// <summary>Client-side validation rules (required, pattern, minlength, etc.).</summary>
    public List<string> ClientValidation { get; init; } = [];

    /// <summary>Server-side validation descriptions.</summary>
    public List<string> ServerValidation { get; init; } = [];

    /// <summary>Error messages keyed by condition (required, pattern, server-invalid, etc.).</summary>
    public Dictionary<string, string> Errors { get; init; } = new();

    /// <summary>Inline help text for the field.</summary>
    public string? Help { get; init; }

    /// <summary>Autocomplete attribute value.</summary>
    public string? Autocomplete { get; init; }

    /// <summary>Whether the field is required.</summary>
    public bool Required { get; init; }

    /// <summary>Minimum length constraint.</summary>
    public int? MinLength { get; init; }

    /// <summary>Maximum length constraint.</summary>
    public int? MaxLength { get; init; }
}

namespace LucidSupport.Models;

/// <summary>
///     Runtime context sent by the widget. Contains page state but NEVER field values.
/// </summary>
public sealed record PageContext
{
    /// <summary>Current page URL (path only, e.g., "/checkout/payment").</summary>
    public required string Url { get; init; }

    /// <summary>IDs of currently visible fields.</summary>
    public List<string> VisibleFieldIds { get; init; } = [];

    /// <summary>Field states keyed by selector. Never contains actual values.</summary>
    public Dictionary<string, FieldState> FieldStates { get; init; } = new();

    /// <summary>Current viewport width in pixels.</summary>
    public int? ViewportWidth { get; init; }

    /// <summary>User's question text (if asking via chat).</summary>
    public string? Question { get; init; }
}

/// <summary>
///     State of a single field. Privacy-safe: no actual values, just structural state.
/// </summary>
public sealed record FieldState
{
    /// <summary>Whether the field has any value entered.</summary>
    public bool HasValue { get; init; }

    /// <summary>Whether the field currently shows an error.</summary>
    public bool HasError { get; init; }

    /// <summary>Error text shown by the page (from validation message element, not the value).</summary>
    public string? ErrorText { get; init; }

    /// <summary>Whether the field currently has focus.</summary>
    public bool HasFocus { get; init; }
}

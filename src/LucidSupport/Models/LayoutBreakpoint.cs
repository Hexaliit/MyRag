namespace LucidSupport.Models;

/// <summary>
///     Responsive layout information for a named breakpoint.
/// </summary>
public sealed record LayoutBreakpoint
{
    /// <summary>Breakpoint name (mobile, tablet, desktop).</summary>
    public required string Name { get; init; }

    /// <summary>Minimum viewport width in pixels (null for smallest breakpoint).</summary>
    public int? MinWidth { get; init; }

    /// <summary>Maximum viewport width in pixels (null for largest breakpoint).</summary>
    public int? MaxWidth { get; init; }

    /// <summary>Number of layout columns at this breakpoint.</summary>
    public int Columns { get; init; } = 1;

    /// <summary>Optional note about the layout at this breakpoint.</summary>
    public string? Note { get; init; }
}

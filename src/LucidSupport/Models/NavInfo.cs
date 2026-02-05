namespace LucidSupport.Models;

/// <summary>
///     Navigation link information (back/next/cancel).
/// </summary>
public sealed record NavInfo
{
    /// <summary>Human-readable label (e.g., "Back to Shipping").</summary>
    public required string Label { get; init; }

    /// <summary>CSS selector for the navigation element.</summary>
    public required string Selector { get; init; }
}

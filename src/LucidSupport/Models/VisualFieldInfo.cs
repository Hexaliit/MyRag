namespace LucidSupport.Models;

/// <summary>
///     Visual analysis of a single field - how it looks, its grouping, its visual state.
///     Captured via Playwright CSS computed styles and screenshots.
/// </summary>
public sealed record VisualFieldInfo
{
    /// <summary>CSS selector matching the field.</summary>
    public required string Selector { get; init; }

    /// <summary>Bounding box (page coordinates).</summary>
    public BoundingBox Bounds { get; init; } = new();

    /// <summary>Visual group this field belongs to (detected via proximity + styling).</summary>
    public string? GroupName { get; init; }

    /// <summary>Index within its visual group (0-based).</summary>
    public int GroupIndex { get; init; }

    /// <summary>Visual styling analysis.</summary>
    public FieldVisualStyle Style { get; init; } = new();

    /// <summary>Error state visual indicators.</summary>
    public ErrorVisualState ErrorState { get; init; } = new();

    /// <summary>Visual prominence score (0.0-1.0): larger, higher-contrast = more prominent.</summary>
    public double Prominence { get; init; }

    /// <summary>Screenshot path of this field region (relative to output dir).</summary>
    public string? ScreenshotPath { get; init; }
}

/// <summary>
///     Bounding box in page coordinates.
/// </summary>
public sealed record BoundingBox
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>
///     Visual styling information extracted from computed CSS styles.
/// </summary>
public sealed record FieldVisualStyle
{
    /// <summary>Background color of the field.</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>Border color of the field.</summary>
    public string? BorderColor { get; init; }

    /// <summary>Border width in pixels.</summary>
    public double BorderWidth { get; init; }

    /// <summary>Border radius in pixels (rounded corners).</summary>
    public double BorderRadius { get; init; }

    /// <summary>Font size in pixels.</summary>
    public double FontSize { get; init; }

    /// <summary>Font family.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Text color.</summary>
    public string? Color { get; init; }

    /// <summary>Whether the field has a visible border.</summary>
    public bool HasBorder { get; init; }

    /// <summary>Whether the field has a shadow (e.g. focus ring).</summary>
    public bool HasShadow { get; init; }

    /// <summary>Whether the field has a visible outline.</summary>
    public bool HasOutline { get; init; }

    /// <summary>Padding (top, right, bottom, left) in pixels.</summary>
    public double[] Padding { get; init; } = [];

    /// <summary>Detected visual theme: "light", "dark", "custom".</summary>
    public string Theme { get; init; } = "light";
}

/// <summary>
///     Visual indicators of error state on a field.
///     Detected by checking CSS changes, icon presence, color changes.
/// </summary>
public sealed record ErrorVisualState
{
    /// <summary>Whether the field appears to be in an error state.</summary>
    public bool IsError { get; init; }

    /// <summary>Border color changes to red/error color.</summary>
    public bool HasErrorBorder { get; init; }

    /// <summary>An error icon is visible near the field.</summary>
    public bool HasErrorIcon { get; init; }

    /// <summary>A visible error message element exists.</summary>
    public bool HasErrorMessage { get; init; }

    /// <summary>The text of the visible error message.</summary>
    public string? ErrorMessageText { get; init; }

    /// <summary>CSS selector of the error message element.</summary>
    public string? ErrorMessageSelector { get; init; }

    /// <summary>Error message position relative to field (above, below, right, inline).</summary>
    public string? ErrorMessagePosition { get; init; }
}

/// <summary>
///     Visual analysis of the entire page - sections, hierarchy, reading order.
/// </summary>
public sealed record PageVisualAnalysis
{
    /// <summary>Screenshot path of the full page.</summary>
    public string? FullScreenshotPath { get; init; }

    /// <summary>Detected visual sections/groups of fields.</summary>
    public List<VisualGroup> Groups { get; init; } = [];

    /// <summary>Per-field visual analysis.</summary>
    public List<VisualFieldInfo> Fields { get; init; } = [];

    /// <summary>Detected page visual theme.</summary>
    public string Theme { get; init; } = "light";

    /// <summary>Whether the page uses a multi-column layout.</summary>
    public bool IsMultiColumn { get; init; }

    /// <summary>Detected primary brand color (if any).</summary>
    public string? PrimaryColor { get; init; }

    /// <summary>Reading order of field selectors (top-to-bottom, left-to-right).</summary>
    public List<string> ReadingOrder { get; init; } = [];
}

/// <summary>
///     A visual group of related fields (e.g., "Payment Details", "Billing Address").
/// </summary>
public sealed record VisualGroup
{
    /// <summary>Group name (from legend, heading, or inferred).</summary>
    public required string Name { get; init; }

    /// <summary>CSS selector for the group container.</summary>
    public string? Selector { get; init; }

    /// <summary>Field selectors in this group, in visual order.</summary>
    public List<string> FieldSelectors { get; init; } = [];

    /// <summary>Bounding box containing all fields in the group.</summary>
    public BoundingBox Bounds { get; init; } = new();

    /// <summary>Whether the group has a visible border/background distinguishing it.</summary>
    public bool HasVisualBoundary { get; init; }
}

namespace LucidSupport.Models;

/// <summary>
///     In-memory representation of a parsed .support.md file.
///     This is the central model for a learned page.
/// </summary>
public sealed record PageModel
{
    // --- Frontmatter metadata ---

    /// <summary>Unique page identifier (e.g., "checkout-payment").</summary>
    public required string PageId { get; init; }

    /// <summary>URL glob pattern to match this page (e.g., "/checkout/payment*").</summary>
    public required string UrlPattern { get; init; }

    /// <summary>Human-readable page title.</summary>
    public required string Title { get; init; }

    /// <summary>When the page was learned.</summary>
    public DateTimeOffset Learned { get; init; }

    /// <summary>Site identifier (e.g., "myapp.example.com").</summary>
    public string? Site { get; init; }

    /// <summary>Responsive layout breakpoints.</summary>
    public List<LayoutBreakpoint> Layouts { get; init; } = [];

    /// <summary>Navigation links (back, next, cancel, etc.).</summary>
    public Dictionary<string, NavInfo> Nav { get; init; } = new();

    // --- Flow metadata (from --follow-nav) ---

    /// <summary>Multi-page flow name (e.g., "checkout").</summary>
    public string? Flow { get; init; }

    /// <summary>Step position in the flow (1-based).</summary>
    public int? Step { get; init; }

    /// <summary>Previous page_id in the flow.</summary>
    public string? Prev { get; init; }

    /// <summary>Next page_id in the flow.</summary>
    public string? Next { get; init; }

    // --- Content sections ---

    /// <summary>Free-text page description (between frontmatter and ## Fields).</summary>
    public string? Description { get; init; }

    /// <summary>Interactive fields on the page.</summary>
    public List<FieldDefinition> Fields { get; init; } = [];

    /// <summary>Contextual condition rules.</summary>
    public List<ConditionRule> Conditions { get; init; } = [];

    /// <summary>FAQ topic mappings to KB articles.</summary>
    public List<TopicMapping> Topics { get; init; } = [];

    // --- Workflow (section grouping + visibility rules) ---

    /// <summary>Logical field groupings for workflow visibility rules.</summary>
    public List<Section> Sections { get; init; } = [];

    /// <summary>Visibility workflow rules (show/hide sections or fields based on state).</summary>
    public List<WorkflowRule> WorkflowRules { get; init; } = [];

    // --- Escalation ---

    /// <summary>Escalation configuration (plugin, threshold, webhook URL).</summary>
    public EscalationConfig? Escalation { get; init; }
}

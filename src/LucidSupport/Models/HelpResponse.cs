namespace LucidSupport.Models;

/// <summary>
///     Response returned by the help API to the widget.
/// </summary>
public sealed record HelpResponse
{
    /// <summary>The help text to display (composed by LLM or template engine).</summary>
    public required string Text { get; init; }

    /// <summary>Elements to highlight in the page.</summary>
    public List<HighlightTarget> Highlights { get; init; } = [];

    /// <summary>Actionable suggestion chips.</summary>
    public List<string> Suggestions { get; init; } = [];

    /// <summary>Related topic links.</summary>
    public List<TopicLink> Topics { get; init; } = [];

    /// <summary>Source identifier for attribution (e.g., "checkout-payment/#card-number").</summary>
    public string? Source { get; init; }
}

/// <summary>
///     A page element to highlight.
/// </summary>
public sealed record HighlightTarget
{
    /// <summary>CSS selector of the element to highlight.</summary>
    public required string Selector { get; init; }

    /// <summary>Highlight style (error, info, success).</summary>
    public string Style { get; init; } = "info";
}

/// <summary>
///     Link to a help topic or KB article.
/// </summary>
public sealed record TopicLink
{
    /// <summary>Topic/article identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display label for the link.</summary>
    public required string Label { get; init; }
}

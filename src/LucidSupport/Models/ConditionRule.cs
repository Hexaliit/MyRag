namespace LucidSupport.Models;

/// <summary>
///     A contextual condition rule from the ## Conditions section.
///     Blockquoted rules with when/suggest/highlight.
/// </summary>
public sealed record ConditionRule
{
    /// <summary>
    ///     Condition expression, e.g. "[#card-number].error AND user.attempts > 2"
    ///     or "page.idle > 30s AND form.incomplete".
    /// </summary>
    public required string When { get; init; }

    /// <summary>Suggestion text to show when the condition matches.</summary>
    public required string Suggest { get; init; }

    /// <summary>Optional CSS selector to highlight when the condition matches.</summary>
    public string? Highlight { get; init; }
}

namespace LucidSupport.Models;

/// <summary>
///     A visibility workflow rule from the ## Workflow section.
///     Controls when fields or sections should be shown or hidden.
/// </summary>
public sealed record WorkflowRule
{
    /// <summary>
    ///     Condition expression, e.g. "[#same-as-billing].checked" or
    ///     "[#payment-method].value == \"invoice\"".
    /// </summary>
    public required string When { get; init; }

    /// <summary>Action to take: "show" or "hide".</summary>
    public required string Action { get; init; }

    /// <summary>Target section id or field selector.</summary>
    public required string Target { get; init; }

    /// <summary>Priority for rule ordering (lower = evaluated first).</summary>
    public int Priority { get; init; }
}

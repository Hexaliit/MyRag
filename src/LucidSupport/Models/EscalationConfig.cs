namespace LucidSupport.Models;

/// <summary>
///     Escalation configuration from YAML frontmatter.
///     Defines when and how to escalate to a human.
/// </summary>
public sealed record EscalationConfig
{
    /// <summary>Plugin name (e.g., "webhook", "console").</summary>
    public string Plugin { get; init; } = "console";

    /// <summary>Webhook URL for the webhook plugin.</summary>
    public string? Url { get; init; }

    /// <summary>Frustration score threshold to auto-suggest escalation.</summary>
    public double Threshold { get; init; } = 8.0;
}

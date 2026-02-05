using LucidSupport.Models;

namespace LucidSupport.Services.Escalation;

/// <summary>
///     Plugin interface for escalating user issues to human support.
/// </summary>
public interface IEscalationPlugin
{
    /// <summary>Plugin display name.</summary>
    string Name { get; }

    /// <summary>Escalate a user issue via this plugin's channel.</summary>
    Task<EscalationResult> EscalateAsync(EscalationContext context, CancellationToken ct = default);
}

/// <summary>Context passed to escalation plugins with all relevant state.</summary>
public sealed record EscalationContext(
    string PageId,
    string Url,
    Dictionary<string, FieldState> FieldStates,
    double FrustrationScore,
    string? UserMessage,
    List<string> ConversationHistory);

/// <summary>Result from an escalation attempt.</summary>
public sealed record EscalationResult(
    bool Accepted,
    string? Message,
    string? TicketUrl);

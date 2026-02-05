using Microsoft.Extensions.Logging;

namespace LucidSupport.Services.Escalation;

/// <summary>
///     Escalation plugin that logs to console. Useful for demo and testing.
/// </summary>
internal sealed class ConsoleEscalationPlugin(ILogger<ConsoleEscalationPlugin> logger) : IEscalationPlugin
{
    public string Name => "console";

    public Task<EscalationResult> EscalateAsync(EscalationContext context, CancellationToken ct = default)
    {
        logger.LogWarning(
            "ESCALATION: page={PageId} url={Url} frustration={Score:F1} message={Message}",
            context.PageId, context.Url, context.FrustrationScore, context.UserMessage);

        logger.LogInformation("  Fields with errors: {Fields}",
            string.Join(", ", context.FieldStates
                .Where(kv => kv.Value.HasError)
                .Select(kv => kv.Key)));

        if (context.ConversationHistory.Count > 0)
        {
            logger.LogInformation("  Conversation ({Count} messages):", context.ConversationHistory.Count);
            foreach (var msg in context.ConversationHistory.TakeLast(3))
                logger.LogInformation("    {Message}", msg);
        }

        return Task.FromResult(new EscalationResult(
            Accepted: true,
            Message: "Your request has been logged. A support agent will follow up shortly.",
            TicketUrl: null));
    }
}

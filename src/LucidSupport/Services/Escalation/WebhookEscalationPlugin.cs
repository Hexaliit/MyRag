using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace LucidSupport.Services.Escalation;

/// <summary>
///     Escalation plugin that POSTs to a configured webhook URL.
/// </summary>
internal sealed class WebhookEscalationPlugin(
    string webhookUrl,
    HttpClient httpClient,
    ILogger<WebhookEscalationPlugin> logger) : IEscalationPlugin
{
    public string Name => "webhook";

    public async Task<EscalationResult> EscalateAsync(EscalationContext context, CancellationToken ct = default)
    {
        var payload = new
        {
            page_id = context.PageId,
            url = context.Url,
            frustration_score = context.FrustrationScore,
            user_message = context.UserMessage,
            error_fields = context.FieldStates
                .Where(kv => kv.Value.HasError)
                .Select(kv => new { field = kv.Key, error = kv.Value.ErrorText })
                .ToList(),
            conversation_history = context.ConversationHistory,
            timestamp = DateTimeOffset.UtcNow
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync(webhookUrl, payload, ct);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Webhook escalation sent to {Url}: {Status}",
                    webhookUrl, response.StatusCode);

                return new EscalationResult(
                    Accepted: true,
                    Message: "Your support request has been submitted. A team member will respond shortly.",
                    TicketUrl: null);
            }

            logger.LogWarning("Webhook escalation failed: {Status} {Reason}",
                response.StatusCode, response.ReasonPhrase);

            return new EscalationResult(
                Accepted: false,
                Message: "Unable to submit your request right now. Please try again.",
                TicketUrl: null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook escalation error for {Url}", webhookUrl);

            return new EscalationResult(
                Accepted: false,
                Message: "Unable to reach support system. Please try again later.",
                TicketUrl: null);
        }
    }
}

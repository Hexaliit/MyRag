using LucidSupport.Models;
using LucidSupport.Services.AI;
using Microsoft.Extensions.Logging;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     Decorates an inner IResponseEngine with AI-powered feedback.
///     Uses the sentinel model to rewrite field help and summarize knowledge.
///     Gracefully falls back to the inner engine when Ollama is unavailable.
/// </summary>
internal sealed class AiResponseEngine(
    IResponseEngine inner,
    SupportOllamaClient ollamaClient,
    IntentClassifier intentClassifier,
    ILogger<AiResponseEngine> logger) : IResponseEngine
{
    public async Task<HelpResponse> GenerateResponseAsync(PageModel model, PageContext context)
    {
        // Only activate for user questions — passive field help passes through
        if (string.IsNullOrWhiteSpace(context.Question))
            return await inner.GenerateResponseAsync(model, context);

        var intent = await intentClassifier.ClassifyAsync(context.Question);

        // Fast return for greetings and navigation — no LLM needed
        if (intent == SupportIntent.Greeting)
        {
            return new HelpResponse
            {
                Text = $"Hello! I can help you with the {model.Title} page. What would you like to know?",
                Suggestions = model.Fields.Where(f => f.Help is not null).Take(3)
                    .Select(f => $"Help with {f.Label}").ToList(),
                Topics = model.Topics.Select(t => new TopicLink { Id = t.ArticleId, Label = t.Question }).ToList()
            };
        }

        if (intent == SupportIntent.Navigation)
        {
            return new HelpResponse
            {
                Text = "I can help you navigate. What are you looking for?",
                Topics = model.Topics.Select(t => new TopicLink { Id = t.ArticleId, Label = t.Question }).ToList()
            };
        }

        // Get base response from inner engine (template + knowledge)
        var baseResponse = await inner.GenerateResponseAsync(model, context);

        // Try to polish with AI
        try
        {
            string? polished = null;

            if (intent == SupportIntent.FieldHelp)
            {
                polished = await PolishFieldHelp(baseResponse.Text, context.Question);
            }
            else if (intent is SupportIntent.GeneralQuestion or SupportIntent.Troubleshoot)
            {
                polished = await SummarizeResponse(baseResponse.Text, context.Question);
            }

            if (polished is not null)
            {
                return baseResponse with
                {
                    Text = polished,
                    Source = (baseResponse.Source ?? "") + "+ai"
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI polishing failed, returning base response");
        }

        return baseResponse;
    }

    private async Task<string?> PolishFieldHelp(string helpText, string question)
    {
        var prompt = $"""
            The user asked: "{question}"

            Here is the field help information:
            {helpText}

            Rewrite this as a brief, friendly 1-2 sentence answer. Be concise and helpful.
            """;

        return await ollamaClient.GenerateAsync(prompt,
            "You are a helpful form assistant. Give brief, clear answers about form fields.", CancellationToken.None);
    }

    private async Task<string?> SummarizeResponse(string responseText, string question)
    {
        var prompt = $"""
            The user asked: "{question}"

            Here is the relevant information:
            {responseText}

            Summarize this into a clear, helpful answer in 2-3 sentences. Focus on answering the user's question directly.
            """;

        return await ollamaClient.GenerateAsync(prompt,
            "You are a helpful customer support assistant. Give clear, concise answers.", CancellationToken.None);
    }
}

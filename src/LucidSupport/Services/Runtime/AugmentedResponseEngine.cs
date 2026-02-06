using LucidSupport.Models;
using LucidSupport.Services.Knowledge;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     Decorates an inner IResponseEngine with manual knowledge search.
///     When a question is asked, embeds the question, searches the knowledge store,
///     and appends relevant chunks to the response.
/// </summary>
internal sealed class AugmentedResponseEngine(
    IResponseEngine inner,
    ManualKnowledgeStore knowledgeStore,
    IEmbeddingService embeddingService,
    ILogger<AugmentedResponseEngine> logger) : IResponseEngine
{
    public async Task<HelpResponse> GenerateResponseAsync(PageModel model, PageContext context)
    {
        var baseResponse = await inner.GenerateResponseAsync(model, context);

        // Only augment when user asked a question
        if (string.IsNullOrWhiteSpace(context.Question) || knowledgeStore.Count == 0)
            return baseResponse;

        try
        {
            var queryEmbedding = await embeddingService.EmbedAsync(context.Question);
            var chunks = knowledgeStore.Search(queryEmbedding, topK: 3, minSimilarity: 0.35f);

            if (chunks.Length == 0)
                return baseResponse;

            // Append knowledge chunks to the response text
            var knowledgeText = string.Join("\n\n", chunks.Select(c =>
                c.Section is not null ? $"**{c.Section}**: {c.Text}" : c.Text));

            var augmentedText = baseResponse.Text + "\n\n---\n\n" + knowledgeText;

            return baseResponse with
            {
                Text = augmentedText,
                Source = baseResponse.Source ?? "manual-knowledge"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Knowledge search failed, returning base response");
            return baseResponse;
        }
    }
}

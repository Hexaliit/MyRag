using LucidRAG.LLM.Config;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services.Providers;

/// <summary>
///     Named provider wrapper for Anthropic LLM service.
/// </summary>
public class AnthropicProvider : BaseLlmProvider
{
    public AnthropicProvider(
        string name,
        ILlmService inner,
        IPromptService promptService,
        ModelConfig modelConfig,
        BackendConfig backendConfig,
        ILogger<AnthropicProvider> logger)
        : base(name, inner, promptService, modelConfig, backendConfig, logger)
    {
    }

    /// <inheritdoc />
    public override LlmBackendType BackendType => LlmBackendType.Anthropic;
}
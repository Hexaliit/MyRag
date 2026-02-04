using LucidRAG.LLM.Config;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services.Providers;

/// <summary>
///     Named provider wrapper for OpenAI LLM service.
///     Also used for LMStudio (OpenAI-compatible API).
/// </summary>
public class OpenAIProvider : BaseLlmProvider
{
    private readonly LlmBackendType _backendType;

    public OpenAIProvider(
        string name,
        ILlmService inner,
        IPromptService promptService,
        ModelConfig modelConfig,
        BackendConfig backendConfig,
        ILogger<OpenAIProvider> logger,
        bool isLmStudio = false)
        : base(name, inner, promptService, modelConfig, backendConfig, logger)
    {
        _backendType = isLmStudio ? LlmBackendType.LMStudio : LlmBackendType.OpenAI;
    }

    /// <inheritdoc />
    public override LlmBackendType BackendType => _backendType;
}
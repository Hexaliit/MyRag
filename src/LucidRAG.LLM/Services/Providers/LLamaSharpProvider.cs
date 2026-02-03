using LucidRAG.LLM.Config;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services.Providers;

/// <summary>
/// Named provider wrapper for LLamaSharp local GGUF inference.
/// </summary>
public class LLamaSharpProvider : BaseLlmProvider
{
    public LLamaSharpProvider(
        string name,
        ILlmService inner,
        IPromptService promptService,
        ModelConfig modelConfig,
        BackendConfig backendConfig,
        ILogger<LLamaSharpProvider> logger)
        : base(name, inner, promptService, modelConfig, backendConfig, logger)
    {
    }

    /// <inheritdoc />
    public override LlmBackendType BackendType => LlmBackendType.LLamaSharp;
}

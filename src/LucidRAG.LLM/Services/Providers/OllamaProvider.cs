using LucidRAG.LLM.Config;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services.Providers;

/// <summary>
///     Named provider wrapper for Ollama LLM service.
/// </summary>
public class OllamaProvider : BaseLlmProvider
{
    public OllamaProvider(
        string name,
        ILlmService inner,
        IPromptService promptService,
        ModelConfig modelConfig,
        BackendConfig backendConfig,
        ILogger<OllamaProvider> logger)
        : base(name, inner, promptService, modelConfig, backendConfig, logger)
    {
    }

    /// <inheritdoc />
    public override LlmBackendType BackendType => LlmBackendType.Ollama;
}
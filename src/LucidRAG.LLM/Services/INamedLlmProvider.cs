using LucidRAG.LLM.Config;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services;

/// <summary>
///     Extended LLM service interface supporting named provider resolution
///     and prompt-based generation.
/// </summary>
public interface INamedLlmProvider : ILlmService
{
    /// <summary>
    ///     Unique name for this provider instance (e.g., "fast-local", "smart-cloud").
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Backend type (Ollama, Anthropic, OpenAI, LMStudio).
    /// </summary>
    LlmBackendType BackendType { get; }

    /// <summary>
    ///     Model identifier configured for this provider.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    ///     Whether this provider supports vision/image input.
    /// </summary>
    bool SupportsVision { get; }

    /// <summary>
    ///     Generate using a named prompt with variable substitution.
    /// </summary>
    /// <param name="promptName">Name of the prompt from prompts.yaml</param>
    /// <param name="variables">Variables to substitute into the template</param>
    /// <param name="options">Optional overrides for generation options</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Generated text response</returns>
    Task<string> GenerateWithPromptAsync(
        string promptName,
        Dictionary<string, object> variables,
        LlmOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Generate JSON using a named prompt with variable substitution.
    /// </summary>
    /// <typeparam name="T">Type to deserialize the response into</typeparam>
    /// <param name="promptName">Name of the prompt from prompts.yaml</param>
    /// <param name="variables">Variables to substitute into the template</param>
    /// <param name="options">Optional overrides for generation options</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Deserialized response object</returns>
    Task<T?> GenerateJsonWithPromptAsync<T>(
        string promptName,
        Dictionary<string, object> variables,
        LlmOptions? options = null,
        CancellationToken ct = default) where T : class;
}
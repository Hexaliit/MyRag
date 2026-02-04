using LucidRAG.LLM.Config;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services;

/// <summary>
///     Service for named prompt resolution and template rendering.
/// </summary>
public interface IPromptService
{
    /// <summary>
    ///     Get a prompt definition by name.
    /// </summary>
    /// <param name="name">Prompt name</param>
    /// <returns>Prompt definition or null if not found</returns>
    PromptDefinition? GetPrompt(string name);

    /// <summary>
    ///     Render a template with variable substitution.
    /// </summary>
    /// <param name="template">Template string with {variable} placeholders</param>
    /// <param name="variables">Variables to substitute</param>
    /// <returns>Rendered template</returns>
    string RenderTemplate(string template, Dictionary<string, object> variables);

    /// <summary>
    ///     Get LlmOptions configured for a specific prompt and backend.
    /// </summary>
    /// <param name="promptName">Prompt name</param>
    /// <param name="backendType">Backend type for overrides</param>
    /// <returns>Configured LlmOptions</returns>
    LlmOptions GetOptionsForPrompt(string promptName, LlmBackendType backendType);

    /// <summary>
    ///     Render a named prompt with variables.
    /// </summary>
    /// <param name="promptName">Prompt name</param>
    /// <param name="variables">Variables to substitute</param>
    /// <param name="backendType">Backend type for overrides</param>
    /// <returns>Tuple of (systemPrompt, renderedTemplate)</returns>
    (string? SystemPrompt, string Template) RenderPrompt(
        string promptName,
        Dictionary<string, object> variables,
        LlmBackendType backendType);

    /// <summary>
    ///     Get all registered prompt names.
    /// </summary>
    IReadOnlyList<string> GetPromptNames();
}
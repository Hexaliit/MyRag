using System.Text.RegularExpressions;
using LucidRAG.LLM.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.LLM.Services;

/// <summary>
/// Service for named prompt resolution and template rendering.
/// </summary>
public partial class PromptService : IPromptService
{
    private readonly PromptsConfig _config;
    private readonly ILogger<PromptService> _logger;

    public PromptService(
        IOptions<PromptsConfig> config,
        ILogger<PromptService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public PromptDefinition? GetPrompt(string name)
    {
        if (_config.Prompts.TryGetValue(name, out var prompt))
            return prompt;

        _logger.LogWarning("Prompt '{Name}' not found", name);
        return null;
    }

    /// <inheritdoc />
    public string RenderTemplate(string template, Dictionary<string, object> variables)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var result = TemplateVariableRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (variables.TryGetValue(key, out var value))
                return value?.ToString() ?? "";

            _logger.LogWarning("Template variable '{Key}' not provided", key);
            return match.Value; // Keep placeholder if not found
        });

        return result;
    }

    /// <inheritdoc />
    public LlmOptions GetOptionsForPrompt(string promptName, LlmBackendType backendType)
    {
        var prompt = GetPrompt(promptName);
        if (prompt == null)
            return LlmOptions.Default;

        return new LlmOptions
        {
            SystemPrompt = prompt.GetSystemPrompt(backendType),
            MaxTokens = prompt.GetMaxTokens(backendType),
            Temperature = prompt.GetTemperature(backendType)
        };
    }

    /// <inheritdoc />
    public (string? SystemPrompt, string Template) RenderPrompt(
        string promptName,
        Dictionary<string, object> variables,
        LlmBackendType backendType)
    {
        var prompt = GetPrompt(promptName);
        if (prompt == null)
        {
            _logger.LogError("Cannot render prompt '{Name}' - not found", promptName);
            return (null, "");
        }

        var systemPrompt = prompt.GetSystemPrompt(backendType);
        var template = prompt.GetTemplate(backendType);
        var renderedTemplate = RenderTemplate(template, variables);

        return (systemPrompt, renderedTemplate);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPromptNames() => _config.Prompts.Keys.ToList();

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.Compiled)]
    private static partial Regex TemplateVariableRegex();
}

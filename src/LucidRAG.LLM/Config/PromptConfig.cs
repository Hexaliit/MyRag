using YamlDotNet.Serialization;

namespace LucidRAG.LLM.Config;

/// <summary>
///     Root prompts configuration loaded from prompts.yaml.
/// </summary>
public class PromptsConfig
{
    /// <summary>
    ///     Named prompt definitions.
    /// </summary>
    public Dictionary<string, PromptDefinition> Prompts { get; set; } = new();
}

/// <summary>
///     Named prompt definition.
/// </summary>
public class PromptDefinition
{
    /// <summary>
    ///     Unique name for this prompt.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Human-readable description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Prompt version for tracking changes.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    ///     System prompt (optional).
    /// </summary>
    public string? System { get; set; }

    /// <summary>
    ///     Main prompt template with {variable} placeholders.
    /// </summary>
    public string Template { get; set; } = "";

    /// <summary>
    ///     Whether this prompt expects JSON output.
    /// </summary>
    [YamlMember(Alias = "json_output")]
    public bool JsonOutput { get; set; }

    /// <summary>
    ///     Default provider name for this prompt (optional).
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    ///     Provider-specific overrides.
    /// </summary>
    public Dictionary<string, PromptOverride> Overrides { get; set; } = new();

    /// <summary>
    ///     Get effective system prompt for backend type.
    /// </summary>
    public string? GetSystemPrompt(LlmBackendType backendType)
    {
        var backendName = backendType.ToString().ToLowerInvariant();
        if (Overrides.TryGetValue(backendName, out var @override) && !string.IsNullOrEmpty(@override.System))
            return @override.System;

        return System;
    }

    /// <summary>
    ///     Get effective template for backend type.
    /// </summary>
    public string GetTemplate(LlmBackendType backendType)
    {
        var backendName = backendType.ToString().ToLowerInvariant();
        if (Overrides.TryGetValue(backendName, out var @override) && !string.IsNullOrEmpty(@override.Template))
            return @override.Template;

        return Template;
    }

    /// <summary>
    ///     Get effective max tokens for backend type.
    /// </summary>
    public int? GetMaxTokens(LlmBackendType backendType)
    {
        var backendName = backendType.ToString().ToLowerInvariant();
        if (Overrides.TryGetValue(backendName, out var @override))
            return @override.MaxTokens;

        return null;
    }

    /// <summary>
    ///     Get effective temperature for backend type.
    /// </summary>
    public double? GetTemperature(LlmBackendType backendType)
    {
        var backendName = backendType.ToString().ToLowerInvariant();
        if (Overrides.TryGetValue(backendName, out var @override))
            return @override.Temperature;

        return null;
    }
}

/// <summary>
///     Provider-specific prompt overrides.
/// </summary>
public class PromptOverride
{
    /// <summary>
    ///     Override system prompt.
    /// </summary>
    public string? System { get; set; }

    /// <summary>
    ///     Override template.
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    ///     Override max tokens.
    /// </summary>
    [YamlMember(Alias = "max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>
    ///     Override temperature.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    ///     Response format hint (text, json).
    /// </summary>
    [YamlMember(Alias = "response_format")]
    public string? ResponseFormat { get; set; }
}
using System.Reflection;
using Fluid;

namespace DoomSummarizer.Services;

/// <summary>
///     Loads and renders prompt templates using Liquid (Fluid) for full conditional/loop support.
///     Templates are loaded from: filesystem override (~/.doomsummarizer/prompts/) first,
///     then embedded resources (Resources/prompts/) as fallback.
/// </summary>
public static class PromptTemplateService
{
    private static readonly FluidParser Parser = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string OverridePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".doomsummarizer", "prompts");

    /// <summary>
    ///     Render a named prompt template with the given variables.
    ///     Template names correspond to files in Resources/prompts/ without extension.
    ///     Example: Render("roundup", new { EVIDENCE = "...", TODAY = "...", ... })
    /// </summary>
    public static string Render(string templateName, Dictionary<string, object?> variables)
    {
        var raw = LoadTemplate(templateName);
        if (!Parser.TryParse(raw, out var template, out var error))
            // If Liquid parsing fails, fall back to simple string replacement
            return SimpleFill(raw, variables);

        var context = new TemplateContext(new TemplateOptions
        {
            MemberAccessStrategy = new UnsafeMemberAccessStrategy()
        });

        foreach (var (key, value) in variables) context.SetValue(key, value ?? "");

        return template.Render(context);
    }

    /// <summary>
    ///     Load raw template text. Checks filesystem override first, then embedded resource.
    /// </summary>
    public static string LoadTemplate(string templateName)
    {
        if (Cache.TryGetValue(templateName, out var cached))
            return cached;

        // 1. Check filesystem override
        var overrideFile = Path.Combine(OverridePath, $"{templateName}.txt");
        if (File.Exists(overrideFile))
        {
            var text = File.ReadAllText(overrideFile);
            Cache[templateName] = text;
            return text;
        }

        // 2. Load from embedded resource
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"DoomSummarizer.Resources.prompts.{templateName}.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException(
                $"Prompt template '{templateName}' not found. " +
                $"Expected at '{overrideFile}' or embedded resource '{resourceName}'.");

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Cache[templateName] = content;
        return content;
    }

    /// <summary>
    ///     Clear the template cache (e.g., after user edits a template file).
    /// </summary>
    public static void ClearCache()
    {
        Cache.Clear();
    }

    /// <summary>
    ///     Simple {{KEY}} replacement fallback when Liquid parsing fails.
    /// </summary>
    private static string SimpleFill(string template, Dictionary<string, object?> variables)
    {
        var result = template;
        foreach (var (key, value) in variables)
            result = result.Replace($"{{{{{key}}}}}", value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
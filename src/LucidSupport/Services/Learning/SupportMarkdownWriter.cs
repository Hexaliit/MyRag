using System.Globalization;
using System.Text;
using LucidSupport.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Serializes a <see cref="PageModel"/> back to .support.md format.
///     Produces output that round-trips through the parser: frontmatter in YAML
///     with flow-style mappings for layouts and nav, followed by Markdown content sections.
/// </summary>
internal static class SupportMarkdownWriter
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    ///     Serialize a <see cref="PageModel"/> to a .support.md string.
    /// </summary>
    public static string Write(PageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var sb = new StringBuilder();

        WriteFrontmatter(sb, model);
        WriteBody(sb, model);

        return sb.ToString();
    }

    /// <summary>
    ///     Serialize a <see cref="PageModel"/> and write it to a file.
    /// </summary>
    public static void WriteFile(PageModel model, string filePath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var content = Write(model);
        File.WriteAllText(filePath, content, Encoding.UTF8);
    }

    // ── Frontmatter ────────────────────────────────────────────────────

    private static void WriteFrontmatter(StringBuilder sb, PageModel model)
    {
        sb.AppendLine("---");

        sb.Append("page_id: ").AppendLine(model.PageId);
        sb.Append("url_pattern: ").AppendLine(model.UrlPattern);
        sb.Append("title: ").AppendLine(model.Title);
        sb.Append("learned: ").AppendLine(FormatTimestamp(model.Learned));

        if (!string.IsNullOrEmpty(model.Site))
            sb.Append("site: ").AppendLine(model.Site);

        if (model.Layouts.Count > 0)
            WriteLayouts(sb, model.Layouts);

        if (model.Nav.Count > 0)
            WriteNav(sb, model.Nav);

        if (!string.IsNullOrEmpty(model.Flow))
            sb.Append("flow: ").AppendLine(model.Flow);

        if (model.Step.HasValue)
            sb.Append("step: ").AppendLine(model.Step.Value.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(model.Prev))
            sb.Append("prev: ").AppendLine(model.Prev);

        if (!string.IsNullOrEmpty(model.Next))
            sb.Append("next: ").AppendLine(model.Next);

        if (model.Escalation is not null)
            WriteEscalation(sb, model.Escalation);

        sb.AppendLine("---");
    }

    private static void WriteLayouts(StringBuilder sb, List<LayoutBreakpoint> layouts)
    {
        sb.AppendLine("layouts:");
        foreach (var layout in layouts)
        {
            sb.Append("  ").Append(layout.Name).Append(": ");
            sb.AppendLine(FormatLayoutFlow(layout));
        }
    }

    /// <summary>
    ///     Format a layout breakpoint as a compact YAML flow mapping.
    ///     Example: { max: 767, columns: 1 }
    /// </summary>
    private static string FormatLayoutFlow(LayoutBreakpoint layout)
    {
        var parts = new List<string>();

        if (layout.MinWidth.HasValue)
            parts.Add($"min: {layout.MinWidth.Value}");

        if (layout.MaxWidth.HasValue)
            parts.Add($"max: {layout.MaxWidth.Value}");

        parts.Add($"columns: {layout.Columns}");

        if (!string.IsNullOrEmpty(layout.Note))
            parts.Add($"note: \"{EscapeYamlString(layout.Note)}\"");

        return "{ " + string.Join(", ", parts) + " }";
    }

    private static void WriteNav(StringBuilder sb, Dictionary<string, NavInfo> nav)
    {
        sb.AppendLine("nav:");
        foreach (var (key, info) in nav)
        {
            sb.Append("  ").Append(key).Append(": ");
            sb.Append("{ label: \"").Append(EscapeYamlString(info.Label));
            sb.Append("\", selector: \"").Append(EscapeYamlString(info.Selector));
            sb.AppendLine("\" }");
        }
    }

    // ── Body sections ──────────────────────────────────────────────────

    private static void WriteBody(StringBuilder sb, PageModel model)
    {
        sb.AppendLine();
        sb.Append("# ").AppendLine(model.Title);

        if (!string.IsNullOrWhiteSpace(model.Description))
        {
            sb.AppendLine();
            sb.AppendLine(model.Description.Trim());
        }

        if (model.Fields.Count > 0)
            WriteFields(sb, model.Fields);

        if (model.Sections.Count > 0)
            WriteSections(sb, model.Sections);

        if (model.Conditions.Count > 0)
            WriteConditions(sb, model.Conditions);

        if (model.Topics.Count > 0)
            WriteTopics(sb, model.Topics);

        if (model.WorkflowRules.Count > 0)
            WriteWorkflow(sb, model.WorkflowRules);
    }

    // ── Fields section ─────────────────────────────────────────────────

    private static void WriteFields(StringBuilder sb, List<FieldDefinition> fields)
    {
        sb.AppendLine();
        sb.AppendLine("## Fields");

        foreach (var field in fields)
        {
            sb.AppendLine();
            sb.Append("### [").Append(field.Selector).Append("] ").AppendLine(field.Label);

            if (!string.IsNullOrEmpty(field.Type))
                sb.Append("- type: ").AppendLine(field.Type);

            if (!string.IsNullOrEmpty(field.DisplayLabel))
                sb.Append("- label: ").AppendLine(field.DisplayLabel);

            if (!string.IsNullOrEmpty(field.Placeholder))
                sb.Append("- placeholder: ").AppendLine(field.Placeholder);

            if (!string.IsNullOrEmpty(field.Pattern))
                sb.Append("- pattern: ").AppendLine(field.Pattern);

            if (field.Required)
                sb.AppendLine("- required: true");

            if (field.MinLength.HasValue)
                sb.Append("- minlength: ").AppendLine(field.MinLength.Value.ToString(CultureInfo.InvariantCulture));

            if (field.MaxLength.HasValue)
                sb.Append("- maxlength: ").AppendLine(field.MaxLength.Value.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(field.Autocomplete))
                sb.Append("- autocomplete: ").AppendLine(field.Autocomplete);

            if (field.ClientValidation.Count > 0 || field.ServerValidation.Count > 0)
                WriteValidation(sb, field);

            if (field.Errors.Count > 0)
                WriteErrors(sb, field.Errors);

            if (!string.IsNullOrEmpty(field.Help))
                sb.Append("- help: ").AppendLine(field.Help);
        }
    }

    private static void WriteValidation(StringBuilder sb, FieldDefinition field)
    {
        sb.AppendLine("- validation:");

        if (field.ClientValidation.Count > 0)
            sb.Append("  - client: ").AppendLine(string.Join(", ", field.ClientValidation));

        if (field.ServerValidation.Count > 0)
            sb.Append("  - server: ").AppendLine(string.Join(", ", field.ServerValidation));
    }

    private static void WriteErrors(StringBuilder sb, Dictionary<string, string> errors)
    {
        sb.AppendLine("- errors:");

        foreach (var (key, message) in errors)
            sb.Append("  - ").Append(key).Append(": \"").Append(EscapeMarkdownQuote(message)).AppendLine("\"");
    }

    // ── Conditions section ─────────────────────────────────────────────

    private static void WriteConditions(StringBuilder sb, List<ConditionRule> conditions)
    {
        sb.AppendLine();
        sb.AppendLine("## Conditions");

        foreach (var condition in conditions)
        {
            sb.AppendLine();
            sb.Append("> when: ").AppendLine(condition.When);
            sb.Append("> suggest: ").AppendLine(condition.Suggest);

            if (!string.IsNullOrEmpty(condition.Highlight))
                sb.Append("> highlight: ").AppendLine(condition.Highlight);
        }
    }

    // ── Topics section ─────────────────────────────────────────────────

    private static void WriteTopics(StringBuilder sb, List<TopicMapping> topics)
    {
        sb.AppendLine();
        sb.AppendLine("## Topics");

        foreach (var topic in topics)
            sb.Append("- \"").Append(EscapeMarkdownQuote(topic.Question)).Append("\" → ").AppendLine(topic.ArticleId);
    }

    // ── Sections section ────────────────────────────────────────────────

    private static void WriteSections(StringBuilder sb, List<Section> sections)
    {
        sb.AppendLine();
        sb.AppendLine("## Sections");

        foreach (var section in sections)
        {
            sb.AppendLine();
            sb.Append("### [").Append(section.Id).Append("] ").AppendLine(section.Label);
            sb.Append("- fields: ").AppendLine(string.Join(", ", section.Fields));
            sb.Append("- order: ").AppendLine(section.Order.ToString(CultureInfo.InvariantCulture));
        }
    }

    // ── Workflow section ────────────────────────────────────────────────

    private static void WriteWorkflow(StringBuilder sb, List<WorkflowRule> rules)
    {
        sb.AppendLine();
        sb.AppendLine("## Workflow");

        foreach (var rule in rules)
        {
            sb.AppendLine();
            sb.Append("> when: ").AppendLine(rule.When);
            sb.Append("> action: ").AppendLine(rule.Action);
            sb.Append("> target: ").AppendLine(rule.Target);

            if (rule.Priority != 0)
                sb.Append("> priority: ").AppendLine(rule.Priority.ToString(CultureInfo.InvariantCulture));
        }
    }

    // ── Escalation frontmatter ──────────────────────────────────────────

    private static void WriteEscalation(StringBuilder sb, EscalationConfig config)
    {
        sb.AppendLine("escalation:");
        sb.Append("  plugin: ").AppendLine(config.Plugin);

        if (!string.IsNullOrEmpty(config.Url))
            sb.Append("  url: ").AppendLine(config.Url);

        sb.Append("  threshold: ").AppendLine(config.Threshold.ToString("F1", CultureInfo.InvariantCulture));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string FormatTimestamp(DateTimeOffset dto)
        => dto.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    private static string EscapeYamlString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeMarkdownQuote(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

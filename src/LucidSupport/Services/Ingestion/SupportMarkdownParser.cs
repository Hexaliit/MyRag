using System.Globalization;
using System.Text.RegularExpressions;

using LucidSupport.Models;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidSupport.Services.Ingestion;

/// <summary>
///     Parses .support.md files into <see cref="PageModel" /> objects.
///     The expected format is:
///     1. YAML frontmatter between --- lines
///     2. Free-text description before ## Fields
///     3. ## Fields section with ### [#selector] Label sub-sections
///     4. ## Conditions section with blockquoted rules
///     5. ## Topics section with "question" -> article-id lines
/// </summary>
internal static partial class SupportMarkdownParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    ///     Parses a .support.md file from the given file path.
    /// </summary>
    public static PageModel ParseFile(string filePath)
    {
        var markdown = File.ReadAllText(filePath);
        return Parse(markdown);
    }

    /// <summary>
    ///     Parses a .support.md markdown string into a <see cref="PageModel" />.
    /// </summary>
    public static PageModel Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new PageModel
            {
                PageId = string.Empty,
                UrlPattern = string.Empty,
                Title = string.Empty
            };
        }

        var lines = markdown.Split('\n');
        var (frontmatter, contentStartLine) = ExtractFrontmatter(lines);
        var dto = frontmatter is not null
            ? YamlDeserializer.Deserialize<FrontmatterDto>(frontmatter)
            : new FrontmatterDto();

        // Split the remaining content into sections
        var sections = SplitSections(lines, contentStartLine);

        var description = BuildDescription(sections);
        var fields = ParseFieldsSection(sections);
        var pageSections = ParseSectionsSection(sections);
        var conditions = ParseConditionsSection(sections);
        var topics = ParseTopicsSection(sections);
        var workflowRules = ParseWorkflowSection(sections);

        return new PageModel
        {
            PageId = dto.PageId ?? string.Empty,
            UrlPattern = dto.UrlPattern ?? string.Empty,
            Title = dto.Title ?? string.Empty,
            Learned = dto.Learned ?? DateTimeOffset.MinValue,
            Site = dto.Site,
            Layouts = ConvertLayouts(dto.Layouts),
            Nav = ConvertNav(dto.Nav),
            Flow = dto.Flow,
            Step = dto.Step,
            Prev = dto.Prev,
            Next = dto.Next,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Fields = fields,
            Sections = pageSections,
            Conditions = conditions,
            Topics = topics,
            WorkflowRules = workflowRules,
            Escalation = ConvertEscalation(dto.Escalation)
        };
    }

    /// <summary>
    ///     Extracts YAML frontmatter from the lines. Returns the YAML string and the
    ///     line index where content begins after the closing --- delimiter.
    /// </summary>
    private static (string? yaml, int contentStart) ExtractFrontmatter(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (null, 0);

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                var yamlLines = lines[1..i];
                var yaml = string.Join('\n', yamlLines);
                return (yaml, i + 1);
            }
        }

        // No closing --- found, treat the entire file as content with no frontmatter
        return (null, 0);
    }

    /// <summary>
    ///     Splits content lines into named sections keyed by ## heading text.
    ///     Lines before any ## heading are stored under the empty-string key.
    /// </summary>
    private static Dictionary<string, List<string>> SplitSections(string[] lines, int startLine)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;
        sections[currentSection] = [];

        for (var i = startLine; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("## ", StringComparison.Ordinal) && !trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                currentSection = trimmed[3..].Trim();
                if (!sections.ContainsKey(currentSection))
                    sections[currentSection] = [];
            }
            else
            {
                sections[currentSection].Add(trimmed);
            }
        }

        return sections;
    }

    /// <summary>
    ///     Builds the free-text description from lines before any ## heading.
    /// </summary>
    private static string? BuildDescription(Dictionary<string, List<string>> sections)
    {
        if (!sections.TryGetValue(string.Empty, out var lines) || lines.Count == 0)
            return null;

        var text = string.Join('\n', lines).Trim();
        return text.Length > 0 ? text : null;
    }

    // ──────────────────────────────────────────────
    //  ## Fields parsing
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Parses the ## Fields section into a list of <see cref="FieldDefinition" /> objects.
    ///     Each field is a ### [#selector] Label sub-heading followed by property lines.
    /// </summary>
    private static List<FieldDefinition> ParseFieldsSection(Dictionary<string, List<string>> sections)
    {
        if (!sections.TryGetValue("Fields", out var lines))
            return [];

        var fields = new List<FieldDefinition>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i].TrimEnd('\r');

            // Look for ### [#selector] Label
            var fieldMatch = FieldHeadingRegex().Match(line);
            if (!fieldMatch.Success)
            {
                i++;
                continue;
            }

            var selector = fieldMatch.Groups[1].Value;
            var label = fieldMatch.Groups[2].Value.Trim();
            i++;

            // Collect property lines until the next ### or end of section
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var clientValidation = new List<string>();
            var serverValidation = new List<string>();
            var errors = new Dictionary<string, string>();
            string? currentNested = null;

            while (i < lines.Count)
            {
                var propLine = lines[i].TrimEnd('\r');

                // Stop at next field heading
                if (propLine.TrimStart().StartsWith("### ", StringComparison.Ordinal))
                    break;

                var trimmedProp = propLine.TrimStart();

                // Nested list items under validation: or errors:
                if (currentNested is not null && trimmedProp.StartsWith("- ", StringComparison.Ordinal))
                {
                    var nestedContent = trimmedProp[2..].Trim();

                    // Check indentation: nested items have more leading whitespace than the parent
                    var leadingSpaces = propLine.Length - propLine.TrimStart().Length;
                    if (leadingSpaces >= 4 || (currentNested is "validation" or "errors" && leadingSpaces >= 2))
                    {
                        if (currentNested == "validation")
                        {
                            ParseValidationLine(nestedContent, clientValidation, serverValidation);
                        }
                        else if (currentNested == "errors")
                        {
                            ParseErrorLine(nestedContent, errors);
                        }

                        i++;
                        continue;
                    }
                }

                // Top-level property line: - key: value
                var propMatch = PropertyLineRegex().Match(trimmedProp);
                if (propMatch.Success)
                {
                    var key = propMatch.Groups[1].Value.Trim();
                    var value = propMatch.Groups[2].Value.Trim();

                    if (key.Equals("validation", StringComparison.OrdinalIgnoreCase))
                    {
                        currentNested = "validation";
                        // If there is inline content, it would be unusual but handle it
                        if (!string.IsNullOrWhiteSpace(value))
                            ParseValidationLine(value, clientValidation, serverValidation);
                    }
                    else if (key.Equals("errors", StringComparison.OrdinalIgnoreCase))
                    {
                        currentNested = "errors";
                        if (!string.IsNullOrWhiteSpace(value))
                            ParseErrorLine(value, errors);
                    }
                    else
                    {
                        currentNested = null;
                        props[key] = value;
                    }

                    i++;
                    continue;
                }

                // Blank line or unrecognized line
                if (string.IsNullOrWhiteSpace(trimmedProp))
                {
                    i++;
                    continue;
                }

                // Non-matching line that is not a heading; skip it
                currentNested = null;
                i++;
            }

            fields.Add(BuildFieldDefinition(selector, label, props, clientValidation, serverValidation, errors));
        }

        return fields;
    }

    /// <summary>
    ///     Parses a single nested validation line into client or server validation lists.
    ///     Expected format: "client: rule" or "server: rule".
    /// </summary>
    private static void ParseValidationLine(string content, List<string> client, List<string> server)
    {
        if (content.StartsWith("client:", StringComparison.OrdinalIgnoreCase))
        {
            var rule = content[7..].Trim();
            if (!string.IsNullOrEmpty(rule))
                client.Add(rule);
        }
        else if (content.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
        {
            var rule = content[7..].Trim();
            if (!string.IsNullOrEmpty(rule))
                server.Add(rule);
        }
    }

    /// <summary>
    ///     Parses a single nested error line. Expected format: key: "message" or key: message.
    /// </summary>
    private static void ParseErrorLine(string content, Dictionary<string, string> errors)
    {
        var colonIdx = content.IndexOf(':');
        if (colonIdx <= 0) return;

        var key = content[..colonIdx].Trim();
        var msg = content[(colonIdx + 1)..].Trim();

        // Strip surrounding quotes if present
        if (msg.Length >= 2 && msg[0] == '"' && msg[^1] == '"')
            msg = msg[1..^1];

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(msg))
            errors[key] = msg;
    }

    /// <summary>
    ///     Constructs a <see cref="FieldDefinition" /> from parsed field properties.
    /// </summary>
    private static FieldDefinition BuildFieldDefinition(
        string selector,
        string label,
        Dictionary<string, string> props,
        List<string> clientValidation,
        List<string> serverValidation,
        Dictionary<string, string> errors)
    {
        return new FieldDefinition
        {
            Selector = selector,
            Label = label,
            Type = props.GetValueOrDefault("type"),
            DisplayLabel = props.GetValueOrDefault("label"),
            Placeholder = props.GetValueOrDefault("placeholder"),
            Pattern = props.GetValueOrDefault("pattern"),
            Autocomplete = props.GetValueOrDefault("autocomplete"),
            Help = props.GetValueOrDefault("help"),
            Required = props.TryGetValue("required", out var req) &&
                       req.Equals("true", StringComparison.OrdinalIgnoreCase),
            MinLength = props.TryGetValue("minlength", out var minLen) &&
                        int.TryParse(minLen, CultureInfo.InvariantCulture, out var minVal)
                ? minVal
                : null,
            MaxLength = props.TryGetValue("maxlength", out var maxLen) &&
                        int.TryParse(maxLen, CultureInfo.InvariantCulture, out var maxVal)
                ? maxVal
                : null,
            ClientValidation = clientValidation,
            ServerValidation = serverValidation,
            Errors = errors
        };
    }

    // ──────────────────────────────────────────────
    //  ## Conditions parsing
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Parses the ## Conditions section. Each condition is a block of consecutive
    ///     blockquote lines (> prefix) containing when:, suggest:, and optionally highlight:.
    /// </summary>
    private static List<ConditionRule> ParseConditionsSection(Dictionary<string, List<string>> sections)
    {
        if (!sections.TryGetValue("Conditions", out var lines))
            return [];

        var conditions = new List<ConditionRule>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i].TrimEnd('\r').TrimStart();

            // Start of a blockquote block
            if (!line.StartsWith('>'))
            {
                i++;
                continue;
            }

            // Collect all consecutive blockquote lines
            string? when = null;
            string? suggest = null;
            string? highlight = null;

            while (i < lines.Count)
            {
                var bqLine = lines[i].TrimEnd('\r').TrimStart();
                if (!bqLine.StartsWith('>'))
                    break;

                // Strip the > prefix and any trailing whitespace
                var content = bqLine[1..].TrimStart();

                if (content.StartsWith("when:", StringComparison.OrdinalIgnoreCase))
                    when = content[5..].Trim();
                else if (content.StartsWith("suggest:", StringComparison.OrdinalIgnoreCase))
                    suggest = content[8..].Trim();
                else if (content.StartsWith("highlight:", StringComparison.OrdinalIgnoreCase))
                    highlight = content[10..].Trim();

                i++;
            }

            if (when is not null && suggest is not null)
            {
                conditions.Add(new ConditionRule
                {
                    When = when,
                    Suggest = suggest,
                    Highlight = string.IsNullOrWhiteSpace(highlight) ? null : highlight
                });
            }
        }

        return conditions;
    }

    // ──────────────────────────────────────────────
    //  ## Topics parsing
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Parses the ## Topics section. Each topic is a line of the form:
    ///     - "question text" -> article-id
    ///     The arrow can be -> or the Unicode right arrow character.
    /// </summary>
    private static List<TopicMapping> ParseTopicsSection(Dictionary<string, List<string>> sections)
    {
        if (!sections.TryGetValue("Topics", out var lines))
            return [];

        var topics = new List<TopicMapping>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').TrimStart();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var content = line[2..].Trim();
            var match = TopicLineRegex().Match(content);
            if (match.Success)
            {
                topics.Add(new TopicMapping
                {
                    Question = match.Groups[1].Value.Trim(),
                    ArticleId = match.Groups[2].Value.Trim()
                });
            }
        }

        return topics;
    }

    // ──────────────────────────────────────────────
    //  ## Sections parsing
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Parses the ## Sections block. Each section is a ### [id] Label sub-heading
    ///     followed by property lines (fields:, order:).
    /// </summary>
    private static List<Section> ParseSectionsSection(Dictionary<string, List<string>> sections)
    {
        if (!sections.TryGetValue("Sections", out var lines))
            return [];

        var result = new List<Section>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i].TrimEnd('\r');
            var sectionMatch = SectionHeadingRegex().Match(line);
            if (!sectionMatch.Success)
            {
                i++;
                continue;
            }

            var id = sectionMatch.Groups[1].Value;
            var label = sectionMatch.Groups[2].Value.Trim();
            i++;

            var fields = new List<string>();
            var order = 0;

            while (i < lines.Count)
            {
                var propLine = lines[i].TrimEnd('\r').TrimStart();
                if (propLine.StartsWith("### ", StringComparison.Ordinal))
                    break;

                if (propLine.StartsWith("- fields:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = propLine[9..].Trim();
                    if (!string.IsNullOrEmpty(val))
                        fields.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (propLine.StartsWith("- order:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = propLine[8..].Trim();
                    if (int.TryParse(val, CultureInfo.InvariantCulture, out var o))
                        order = o;
                }

                i++;
            }

            result.Add(new Section
            {
                Id = id,
                Label = label,
                Fields = fields,
                Order = order
            });
        }

        return result;
    }

    // ──────────────────────────────────────────────
    //  ## Workflow parsing
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Parses the ## Workflow section. Each rule is a block of consecutive
    ///     blockquote lines with when:, action:, target:, and optionally priority:.
    /// </summary>
    private static List<WorkflowRule> ParseWorkflowSection(Dictionary<string, List<string>> sections)
    {
        if (!sections.TryGetValue("Workflow", out var lines))
            return [];

        var rules = new List<WorkflowRule>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i].TrimEnd('\r').TrimStart();

            if (!line.StartsWith('>'))
            {
                i++;
                continue;
            }

            string? when = null;
            string? action = null;
            string? target = null;
            var priority = 0;

            while (i < lines.Count)
            {
                var bqLine = lines[i].TrimEnd('\r').TrimStart();
                if (!bqLine.StartsWith('>'))
                    break;

                var content = bqLine[1..].TrimStart();

                if (content.StartsWith("when:", StringComparison.OrdinalIgnoreCase))
                    when = content[5..].Trim();
                else if (content.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
                    action = content[7..].Trim();
                else if (content.StartsWith("target:", StringComparison.OrdinalIgnoreCase))
                    target = content[7..].Trim();
                else if (content.StartsWith("priority:", StringComparison.OrdinalIgnoreCase))
                {
                    var pVal = content[9..].Trim();
                    if (int.TryParse(pVal, CultureInfo.InvariantCulture, out var p))
                        priority = p;
                }

                i++;
            }

            if (when is not null && action is not null && target is not null)
            {
                rules.Add(new WorkflowRule
                {
                    When = when,
                    Action = action,
                    Target = target,
                    Priority = priority
                });
            }
        }

        return rules;
    }

    // ──────────────────────────────────────────────
    //  Escalation conversion
    // ──────────────────────────────────────────────

    private static EscalationConfig? ConvertEscalation(EscalationDto? dto)
    {
        if (dto is null) return null;

        return new EscalationConfig
        {
            Plugin = dto.Plugin ?? "console",
            Url = dto.Url,
            Threshold = dto.Threshold ?? 8.0
        };
    }

    // ──────────────────────────────────────────────
    //  Frontmatter DTO conversion helpers
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Converts the YAML layouts dictionary into a list of <see cref="LayoutBreakpoint" />.
    /// </summary>
    private static List<LayoutBreakpoint> ConvertLayouts(Dictionary<string, LayoutDto>? layouts)
    {
        if (layouts is null || layouts.Count == 0)
            return [];

        var result = new List<LayoutBreakpoint>(layouts.Count);

        foreach (var (name, dto) in layouts)
        {
            result.Add(new LayoutBreakpoint
            {
                Name = name,
                MinWidth = dto.Min,
                MaxWidth = dto.Max,
                Columns = dto.Columns ?? 1,
                Note = dto.Note
            });
        }

        return result;
    }

    /// <summary>
    ///     Converts the YAML nav dictionary into <see cref="NavInfo" /> entries.
    /// </summary>
    private static Dictionary<string, NavInfo> ConvertNav(Dictionary<string, NavDto>? nav)
    {
        if (nav is null || nav.Count == 0)
            return new Dictionary<string, NavInfo>();

        var result = new Dictionary<string, NavInfo>(nav.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, dto) in nav)
        {
            result[key] = new NavInfo
            {
                Label = dto.Label ?? key,
                Selector = dto.Selector ?? string.Empty
            };
        }

        return result;
    }

    // ──────────────────────────────────────────────
    //  Compiled regex patterns
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Matches a field heading: ### [#selector] Label text
    /// </summary>
    [GeneratedRegex(@"^###\s+\[([^\]]+)\]\s+(.+)$")]
    private static partial Regex FieldHeadingRegex();

    /// <summary>
    ///     Matches a section heading: ### [section-id] Label text
    ///     Same regex as FieldHeadingRegex — sections use the same format.
    /// </summary>
    [GeneratedRegex(@"^###\s+\[([^\]]+)\]\s+(.+)$")]
    private static partial Regex SectionHeadingRegex();

    /// <summary>
    ///     Matches a property line: - key: value
    /// </summary>
    [GeneratedRegex(@"^-\s+(\w[\w-]*):\s*(.*)$")]
    private static partial Regex PropertyLineRegex();

    /// <summary>
    ///     Matches a topic mapping line: "question" -> article-id
    ///     Supports both ASCII arrow (->) and Unicode arrow character.
    /// </summary>
    [GeneratedRegex("""^"([^"]+)"\s*(?:->|\u2192)\s*(.+)$""")]
    private static partial Regex TopicLineRegex();

    // ──────────────────────────────────────────────
    //  YAML frontmatter DTOs
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Private DTO for deserializing YAML frontmatter. Property names use snake_case
    ///     to match YAML field names via UnderscoredNamingConvention.
    /// </summary>
    private sealed class FrontmatterDto
    {
        public string? PageId { get; set; }
        public string? UrlPattern { get; set; }
        public string? Title { get; set; }
        public DateTimeOffset? Learned { get; set; }
        public string? Site { get; set; }
        public Dictionary<string, LayoutDto>? Layouts { get; set; }
        public Dictionary<string, NavDto>? Nav { get; set; }
        public string? Flow { get; set; }
        public int? Step { get; set; }
        public string? Prev { get; set; }
        public string? Next { get; set; }
        public EscalationDto? Escalation { get; set; }
    }

    /// <summary>
    ///     DTO for escalation configuration in YAML frontmatter.
    /// </summary>
    private sealed class EscalationDto
    {
        public string? Plugin { get; set; }
        public string? Url { get; set; }
        public double? Threshold { get; set; }
    }

    /// <summary>
    ///     DTO for a layout breakpoint in YAML frontmatter.
    /// </summary>
    private sealed class LayoutDto
    {
        public int? Min { get; set; }
        public int? Max { get; set; }
        public int? Columns { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>
    ///     DTO for a navigation entry in YAML frontmatter.
    /// </summary>
    private sealed class NavDto
    {
        public string? Label { get; set; }
        public string? Selector { get; set; }
    }
}

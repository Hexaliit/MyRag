using System.Text.RegularExpressions;
using LucidSupport.Models;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     Evaluates workflow visibility rules against field state.
///     Returns which sections/fields should be visible and which events to emit.
/// </summary>
internal sealed partial class WorkflowEvaluator
{
    /// <summary>
    ///     Evaluate all workflow rules for the given page model and field states.
    /// </summary>
    public WorkflowResult Evaluate(
        PageModel model,
        Dictionary<string, FieldWorkflowState> fieldStates,
        string? currentSection = null)
    {
        var visibleFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<WorkflowEvent>();

        // Start with everything visible
        foreach (var field in model.Fields)
            visibleFields.Add(field.Selector);

        foreach (var section in model.Sections)
            visibleSections.Add(section.Id);

        // Apply rules in priority order
        foreach (var rule in model.WorkflowRules.OrderBy(r => r.Priority))
        {
            if (!EvaluateCondition(rule.When, fieldStates))
                continue;

            var isSection = model.Sections.Any(s =>
                s.Id.Equals(rule.Target.TrimStart('[').TrimEnd(']'), StringComparison.OrdinalIgnoreCase));

            if (string.Equals(rule.Action, "hide", StringComparison.OrdinalIgnoreCase))
            {
                if (isSection)
                {
                    var sectionId = rule.Target.TrimStart('[').TrimEnd(']');
                    visibleSections.Remove(sectionId);
                    events.Add(new WorkflowEvent("ls:section:hide", rule.Target, rule.When));

                    // Also hide all fields in this section
                    var section = model.Sections.FirstOrDefault(s =>
                        s.Id.Equals(sectionId, StringComparison.OrdinalIgnoreCase));
                    if (section is not null)
                    {
                        foreach (var f in section.Fields)
                            visibleFields.Remove(f);
                    }
                }
                else
                {
                    visibleFields.Remove(rule.Target);
                    events.Add(new WorkflowEvent("ls:field:hide", rule.Target, rule.When));
                }
            }
            else if (string.Equals(rule.Action, "show", StringComparison.OrdinalIgnoreCase))
            {
                if (isSection)
                {
                    var sectionId = rule.Target.TrimStart('[').TrimEnd(']');
                    visibleSections.Add(sectionId);
                    events.Add(new WorkflowEvent("ls:section:show", rule.Target, rule.When));

                    var section = model.Sections.FirstOrDefault(s =>
                        s.Id.Equals(sectionId, StringComparison.OrdinalIgnoreCase));
                    if (section is not null)
                    {
                        foreach (var f in section.Fields)
                            visibleFields.Add(f);
                    }
                }
                else
                {
                    visibleFields.Add(rule.Target);
                    events.Add(new WorkflowEvent("ls:field:show", rule.Target, rule.When));
                }
            }
        }

        return new WorkflowResult(
            [.. visibleFields],
            [.. visibleSections],
            events);
    }

    /// <summary>
    ///     Evaluates a workflow condition expression against field states.
    ///     Supports: [#selector].checked, [#selector].value == "x", [#selector].empty,
    ///     [#selector].hasValue, and AND/OR combinations.
    /// </summary>
    internal static bool EvaluateCondition(string expression, Dictionary<string, FieldWorkflowState> fieldStates)
    {
        // Handle AND combinations
        if (expression.Contains(" AND ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = expression.Split([" AND "], StringSplitOptions.RemoveEmptyEntries);
            return parts.All(p => EvaluateCondition(p.Trim(), fieldStates));
        }

        // Handle OR combinations
        if (expression.Contains(" OR ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = expression.Split([" OR "], StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(p => EvaluateCondition(p.Trim(), fieldStates));
        }

        var expr = expression.Trim();

        // [#selector].checked
        var checkedMatch = CheckedRegex().Match(expr);
        if (checkedMatch.Success)
        {
            var selector = checkedMatch.Groups[1].Value;
            return fieldStates.TryGetValue(selector, out var state) && state.IsChecked;
        }

        // [#selector].value == "value"
        var valueMatch = ValueEqualsRegex().Match(expr);
        if (valueMatch.Success)
        {
            var selector = valueMatch.Groups[1].Value;
            var expected = valueMatch.Groups[2].Value;
            return fieldStates.TryGetValue(selector, out var state) &&
                   string.Equals(state.Value, expected, StringComparison.OrdinalIgnoreCase);
        }

        // [#selector].empty
        var emptyMatch = EmptyRegex().Match(expr);
        if (emptyMatch.Success)
        {
            var selector = emptyMatch.Groups[1].Value;
            return !fieldStates.TryGetValue(selector, out var state) || string.IsNullOrEmpty(state.Value);
        }

        // [#selector].hasValue
        var hasValueMatch = HasValueRegex().Match(expr);
        if (hasValueMatch.Success)
        {
            var selector = hasValueMatch.Groups[1].Value;
            return fieldStates.TryGetValue(selector, out var state) && !string.IsNullOrEmpty(state.Value);
        }

        return false;
    }

    [GeneratedRegex(@"^\[([^\]]+)\]\.checked$", RegexOptions.IgnoreCase)]
    private static partial Regex CheckedRegex();

    [GeneratedRegex(@"^\[([^\]]+)\]\.value\s*==\s*""([^""]*)""$", RegexOptions.IgnoreCase)]
    private static partial Regex ValueEqualsRegex();

    [GeneratedRegex(@"^\[([^\]]+)\]\.empty$", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyRegex();

    [GeneratedRegex(@"^\[([^\]]+)\]\.hasValue$", RegexOptions.IgnoreCase)]
    private static partial Regex HasValueRegex();
}

/// <summary>Workflow-specific field state that includes values (admin-only, not sent from widget).</summary>
public sealed record FieldWorkflowState
{
    public string? Value { get; init; }
    public bool IsChecked { get; init; }
    public bool HasError { get; init; }
}

/// <summary>A workflow event emitted when a rule matches.</summary>
public sealed record WorkflowEvent(string Type, string Target, string Rule);

/// <summary>Result of evaluating all workflow rules.</summary>
public sealed record WorkflowResult(
    List<string> VisibleFields,
    List<string> VisibleSections,
    List<WorkflowEvent> Events);

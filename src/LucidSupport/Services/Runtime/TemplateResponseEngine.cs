using LucidSupport.Models;
using LucidSupport.Services.Guidance;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     Composes HelpResponse from PageModel + PageContext without any LLM.
///     Uses fuzzy matching against topics, field help text, and condition rules.
/// </summary>
internal sealed class TemplateResponseEngine(ConditionEvaluator conditionEvaluator) : IResponseEngine
{
    private readonly ConditionEvaluator _conditionEvaluator = conditionEvaluator;
    /// <summary>Generate a contextual help response given a page model and runtime context.</summary>
    public Task<HelpResponse> GenerateResponseAsync(PageModel model, PageContext context)
        => Task.FromResult(GenerateResponse(model, context));

    /// <summary>Generate a contextual help response (synchronous core logic).</summary>
    internal HelpResponse GenerateResponse(PageModel model, PageContext context)
    {
        var text = "";
        var highlights = new List<HighlightTarget>();
        var suggestions = new List<string>();
        var topics = new List<TopicLink>();
        string? source = null;

        // 1. Question asked? → fuzzy-match topics + field labels/help
        if (!string.IsNullOrWhiteSpace(context.Question))
        {
            var questionResult = MatchQuestion(model, context.Question);
            if (questionResult != null)
            {
                text = questionResult.Text;
                highlights.AddRange(questionResult.Highlights);
                source = questionResult.Source;
            }
        }

        // 2. Fields in error? → return field help + error text + highlights
        if (string.IsNullOrEmpty(text))
        {
            var errorResult = MatchErrorFields(model, context);
            if (errorResult != null)
            {
                text = errorResult.Text;
                highlights.AddRange(errorResult.Highlights);
                source = errorResult.Source;
            }
        }

        // 3. Field focused? → return that field's help text
        if (string.IsNullOrEmpty(text))
        {
            var focusResult = MatchFocusedField(model, context);
            if (focusResult != null)
            {
                text = focusResult.Text;
                highlights.AddRange(focusResult.Highlights);
                source = focusResult.Source;
            }
        }

        // 4. Conditions triggered? → append condition text
        if (string.IsNullOrEmpty(text))
        {
            var conditionMatches = _conditionEvaluator.Evaluate(model.Conditions, context);
            if (conditionMatches.Count > 0)
            {
                var first = conditionMatches[0];
                text = first.Suggest;
                if (!string.IsNullOrEmpty(first.Highlight))
                    highlights.Add(new HighlightTarget { Selector = first.Highlight, Style = "info" });
            }
        }

        // 5. Fallback
        if (string.IsNullOrEmpty(text))
        {
            text = $"I can help you with the {model.Title} page. Ask a question or click a suggestion below.";
        }

        // Build topic suggestions from model
        foreach (var topic in model.Topics)
        {
            topics.Add(new TopicLink { Id = topic.ArticleId, Label = topic.Question });
        }

        // Build field-based suggestions for fields that have help text
        foreach (var field in model.Fields.Where(f => !string.IsNullOrEmpty(f.Help)).Take(3))
        {
            suggestions.Add($"Help with {field.Label}");
        }

        // 6. Pattern guidance pass
        var fieldGuidance = GenerateFieldGuidance(model, context);

        return new HelpResponse
        {
            Text = text,
            Highlights = highlights,
            Suggestions = suggestions,
            Topics = topics,
            Source = source,
            FieldGuidance = fieldGuidance
        };
    }

    /// <summary>
    ///     Generate pattern-based guidance for fields based on interaction signals.
    ///     Shows guidance when: field has focus, user is struggling (focus count > 2 without value),
    ///     or user has dwelled for a long time without entering a value.
    /// </summary>
    private static List<FieldGuidance> GenerateFieldGuidance(PageModel model, PageContext context)
    {
        var guidance = new List<FieldGuidance>();

        foreach (var (selector, state) in context.FieldStates)
        {
            var field = model.Fields.FirstOrDefault(f => f.Selector == selector);
            if (field?.Pattern is null) continue;

            var patternGuidance = PatternGuidanceRegistry.Get(field.Pattern);
            if (patternGuidance is null) continue;

            // Show if: has focus, OR struggling (3+ focus visits without value), OR long dwell without value
            var shouldShow = state.HasFocus
                             || (state.FocusCount > 2 && !state.HasValue)
                             || (state.DwellMs > 10_000 && !state.HasValue);

            if (!shouldShow) continue;

            guidance.Add(new FieldGuidance
            {
                Selector = selector,
                Pattern = field.Pattern,
                FormatHint = patternGuidance.FormatHint,
                Example = patternGuidance.Example,
                PrivacyNote = patternGuidance.PrivacyNote,
                IsProactive = !state.HasError
            });
        }

        return guidance;
    }

    private static PartialResponse? MatchQuestion(PageModel model, string question)
    {
        var q = question.ToLowerInvariant();

        // Match against topics first (most specific)
        foreach (var topic in model.Topics)
        {
            if (FuzzyContains(q, topic.Question.ToLowerInvariant()))
            {
                return new PartialResponse(
                    topic.Question,
                    [],
                    $"{model.PageId}/topic/{topic.ArticleId}"
                );
            }
        }

        // Match against field labels and help text
        foreach (var field in model.Fields)
        {
            if (FuzzyContains(q, field.Label.ToLowerInvariant()) ||
                (field.Help != null && FuzzyContains(q, field.Help.ToLowerInvariant())))
            {
                var helpText = field.Help ?? $"The {field.Label} field";
                if (field.Required) helpText += " (required)";

                if (field.Errors.Count > 0)
                {
                    helpText += "\n\nCommon issues:";
                    foreach (var (errorType, msg) in field.Errors)
                    {
                        helpText += $"\n• {msg}";
                    }
                }

                return new PartialResponse(
                    helpText,
                    [new HighlightTarget { Selector = field.Selector, Style = "info" }],
                    $"{model.PageId}/{field.Selector}"
                );
            }
        }

        return null;
    }

    private static PartialResponse? MatchErrorFields(PageModel model, PageContext context)
    {
        var errorFields = context.FieldStates
            .Where(kv => kv.Value.HasError)
            .Select(kv => new { Selector = kv.Key, State = kv.Value })
            .ToList();

        if (errorFields.Count == 0) return null;

        var parts = new List<string>();
        var highlights = new List<HighlightTarget>();

        foreach (var ef in errorFields)
        {
            var field = model.Fields.FirstOrDefault(f => f.Selector == ef.Selector);
            if (field == null) continue;

            var msg = !string.IsNullOrEmpty(ef.State.ErrorText) ? ef.State.ErrorText : null;

            // Try to find a matching error message from the field definition
            if (msg == null && field.Errors.Count > 0)
            {
                msg = field.Errors.Values.First();
            }

            var part = $"**{field.Label}**: {msg ?? "Please check this field."}";
            if (!string.IsNullOrEmpty(field.Help))
                part += $"\n  💡 {field.Help}";

            parts.Add(part);
            highlights.Add(new HighlightTarget { Selector = field.Selector, Style = "error" });
        }

        if (parts.Count == 0) return null;

        var intro = parts.Count == 1
            ? "There's an issue with one field:"
            : $"There are issues with {parts.Count} fields:";

        return new PartialResponse(
            $"{intro}\n\n{string.Join("\n\n", parts)}",
            highlights,
            $"{model.PageId}/errors"
        );
    }

    private static PartialResponse? MatchFocusedField(PageModel model, PageContext context)
    {
        var focused = context.FieldStates.FirstOrDefault(kv => kv.Value.HasFocus);
        if (focused.Key == null) return null;

        var field = model.Fields.FirstOrDefault(f => f.Selector == focused.Key);
        if (field == null) return null;

        var text = field.Help ?? $"Enter your {field.Label.ToLowerInvariant()}.";

        if (!string.IsNullOrEmpty(field.Pattern))
            text += $"\nExpected format: {field.Pattern}";

        if (field.Required)
            text += "\nThis field is required.";

        return new PartialResponse(
            text,
            [new HighlightTarget { Selector = field.Selector, Style = "info" }],
            $"{model.PageId}/{field.Selector}"
        );
    }

    /// <summary>Simple fuzzy contains: checks if any significant words from needle appear in haystack.</summary>
    private static bool FuzzyContains(string haystack, string needle)
    {
        // Split needle into words, ignore short common words
        var words = needle.Split([' ', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToList();

        if (words.Count == 0) return false;

        // At least half the significant words should appear
        var matchCount = words.Count(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        return matchCount >= Math.Max(1, words.Count / 2);
    }

    private sealed record PartialResponse(string Text, List<HighlightTarget> Highlights, string? Source);
}

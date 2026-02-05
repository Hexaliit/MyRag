using LucidSupport.Models;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     Server-side condition rule evaluator. Mirrors the widget's conditions.ts logic
///     so the API can proactively include condition-based help in responses.
/// </summary>
internal static class ConditionEvaluator
{
    /// <summary>Evaluate all conditions against the given page context, returning those that match.</summary>
    public static List<ConditionRule> Evaluate(IReadOnlyList<ConditionRule> rules, PageContext context)
    {
        var matched = new List<ConditionRule>();
        foreach (var rule in rules)
        {
            if (EvaluateRule(rule, context))
                matched.Add(rule);
        }

        return matched;
    }

    private static bool EvaluateRule(ConditionRule rule, PageContext context)
    {
        // Rules support AND-combined parts
        var parts = rule.When.Split([" AND ", " and "], StringSplitOptions.TrimEntries);
        return parts.All(part => EvaluatePart(part, context));
    }

    private static bool EvaluatePart(string expr, PageContext context)
    {
        // [#selector].error — field is in error state
        if (TryMatchFieldCondition(expr, out var selector, out var prop))
        {
            if (!context.FieldStates.TryGetValue(selector, out var fs))
                return false;

            return prop switch
            {
                "error" => fs.HasError,
                "empty" => !fs.HasValue,
                "focus" => fs.HasFocus,
                "changed" => false, // server can't know this
                _ => false
            };
        }

        // page.idle > Ns — we can't evaluate idle time server-side, skip
        if (expr.StartsWith("page.idle", StringComparison.OrdinalIgnoreCase))
            return false;

        // form.incomplete — check if any visible field has no value
        if (expr.Equals("form.incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return context.FieldStates.Values.Any(fs => !fs.HasValue);
        }

        // user.attempts > N — server can't know this
        if (expr.StartsWith("user.attempts", StringComparison.OrdinalIgnoreCase))
            return false;

        return false;
    }

    /// <summary>Parse "[#selector].property" format.</summary>
    private static bool TryMatchFieldCondition(string expr, out string selector, out string property)
    {
        selector = "";
        property = "";

        if (!expr.StartsWith('[')) return false;

        var closeBracket = expr.IndexOf(']');
        if (closeBracket < 0) return false;

        selector = expr[1..closeBracket];

        // Expect ".property" after the bracket
        var rest = expr[(closeBracket + 1)..];
        if (!rest.StartsWith('.')) return false;

        // Take first dotted segment (e.g., ".error.type" → "error")
        property = rest[1..];
        var nextDot = property.IndexOf('.');
        if (nextDot > 0) property = property[..nextDot];

        return selector.Length > 0 && property.Length > 0;
    }
}

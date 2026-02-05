namespace LucidSupport.Services.Ingestion;

/// <summary>
///     Input attributes extracted from a form field element during page learning.
///     Used by <see cref="PatternRegistry" /> to classify the field's data pattern.
/// </summary>
public sealed record FieldAttributes
{
    /// <summary>HTML input type attribute (e.g., "email", "tel", "password").</summary>
    public string? Type { get; init; }

    /// <summary>Element name attribute (e.g., "card-number", "user_email").</summary>
    public string? Name { get; init; }

    /// <summary>Autocomplete attribute value (e.g., "cc-number", "postal-code").</summary>
    public string? Autocomplete { get; init; }

    /// <summary>Placeholder text from the element.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Aria-label attribute value.</summary>
    public string? AriaLabel { get; init; }
}

/// <summary>
///     Detects named data patterns from field attributes (not from actual values).
///     Used during page learning to classify fields into well-known categories
///     such as "email", "credit-card", "phone", etc.
/// </summary>
internal static class PatternRegistry
{
    /// <summary>All known pattern names in detection priority order.</summary>
    public static IReadOnlyList<string> AllPatterns { get; } =
    [
        "email",
        "phone",
        "credit-card",
        "cvv",
        "date-partial",
        "postal-code",
        "password",
        "url",
        "currency",
        "name",
        "address",
        "username",
        "search"
    ];

    private static readonly Func<FieldAttributes, bool>[] EmailRules =
    [
        a => TypeIs(a, "email"),
        a => AutocompleteIs(a, "email"),
        a => NameContains(a, "email")
    ];

    private static readonly Func<FieldAttributes, bool>[] PhoneRules =
    [
        a => TypeIs(a, "tel"),
        a => AutocompleteIs(a, "tel"),
        a => NameContains(a, "phone"),
        a => NameContains(a, "tel")
    ];

    private static readonly Func<FieldAttributes, bool>[] CreditCardRules =
    [
        a => AutocompleteIs(a, "cc-number"),
        a => NameContains(a, "card"),
        a => NameContains(a, "ccn"),
        a => NameContains(a, "pan")
    ];

    private static readonly Func<FieldAttributes, bool>[] CvvRules =
    [
        a => AutocompleteIs(a, "cc-csc"),
        a => NameContains(a, "cvv"),
        a => NameContains(a, "cvc"),
        a => NameContains(a, "csc")
    ];

    private static readonly Func<FieldAttributes, bool>[] DatePartialRules =
    [
        a => NameContains(a, "expir"),
        a => NameContains(a, "mm.yy"),
        a => NameContains(a, "mm/yy"),
        a => PlaceholderContains(a, "MM")
    ];

    private static readonly Func<FieldAttributes, bool>[] PostalCodeRules =
    [
        a => AutocompleteIs(a, "postal-code"),
        a => NameContains(a, "zip"),
        a => NameContains(a, "postcode"),
        a => NameContains(a, "postal")
    ];

    private static readonly Func<FieldAttributes, bool>[] PasswordRules =
    [
        a => TypeIs(a, "password")
    ];

    private static readonly Func<FieldAttributes, bool>[] UrlRules =
    [
        a => TypeIs(a, "url")
    ];

    private static readonly Func<FieldAttributes, bool>[] CurrencyRules =
    [
        a => NameContains(a, "amount"),
        a => NameContains(a, "price"),
        a => NameContains(a, "cost")
    ];

    private static readonly Func<FieldAttributes, bool>[] NameRules =
    [
        a => AutocompleteIs(a, "name"),
        a => AutocompleteIs(a, "given-name"),
        a => AutocompleteIs(a, "family-name"),
        a => NameContains(a, "name") && !NameContains(a, "username")
    ];

    private static readonly Func<FieldAttributes, bool>[] AddressRules =
    [
        a => AutocompleteStartsWith(a, "address-"),
        a => NameContains(a, "address"),
        a => NameContains(a, "street")
    ];

    private static readonly Func<FieldAttributes, bool>[] UsernameRules =
    [
        a => AutocompleteIs(a, "username"),
        a => NameContains(a, "username"),
        a => NameContains(a, "user")
    ];

    private static readonly Func<FieldAttributes, bool>[] SearchRules =
    [
        a => TypeIs(a, "search"),
        a => NameContains(a, "search"),
        a => NameContains(a, "query"),
        a => NameContains(a, "q")
    ];

    /// <summary>
    ///     Ordered table of (pattern name, rules). Checked sequentially so that
    ///     more-specific patterns (e.g. "cvv") are tested before broader ones (e.g. "name").
    /// </summary>
    private static readonly (string Pattern, Func<FieldAttributes, bool>[] Rules)[] DetectionTable =
    [
        ("email", EmailRules),
        ("phone", PhoneRules),
        ("credit-card", CreditCardRules),
        ("cvv", CvvRules),
        ("date-partial", DatePartialRules),
        ("postal-code", PostalCodeRules),
        ("password", PasswordRules),
        ("url", UrlRules),
        ("currency", CurrencyRules),
        ("name", NameRules),
        ("address", AddressRules),
        ("username", UsernameRules),
        ("search", SearchRules)
    ];

    /// <summary>
    ///     Detects a named data pattern from the given field attributes.
    ///     Checks signals in priority order: autocomplete, then type, then name/placeholder.
    ///     Returns the first matching pattern name, or <c>null</c> if no pattern matches.
    /// </summary>
    /// <param name="attrs">The field attributes extracted from the DOM element.</param>
    /// <returns>A pattern name (e.g. "email", "credit-card") or <c>null</c>.</returns>
    public static string? Detect(FieldAttributes attrs)
    {
        ArgumentNullException.ThrowIfNull(attrs);

        foreach (var (pattern, rules) in DetectionTable)
        {
            foreach (var rule in rules)
            {
                if (rule(attrs))
                    return pattern;
            }
        }

        return null;
    }

    // ── Signal helpers ──────────────────────────────────────────────────

    private static bool TypeIs(FieldAttributes a, string value)
        => string.Equals(a.Type, value, StringComparison.OrdinalIgnoreCase);

    private static bool AutocompleteIs(FieldAttributes a, string value)
        => string.Equals(a.Autocomplete, value, StringComparison.OrdinalIgnoreCase);

    private static bool AutocompleteStartsWith(FieldAttributes a, string prefix)
        => a.Autocomplete is not null
           && a.Autocomplete.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool NameContains(FieldAttributes a, string fragment)
        => a.Name is not null
           && a.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool PlaceholderContains(FieldAttributes a, string fragment)
        => a.Placeholder is not null
           && a.Placeholder.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

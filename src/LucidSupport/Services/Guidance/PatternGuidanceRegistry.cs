namespace LucidSupport.Services.Guidance;

/// <summary>
///     Provides format hints, examples, and privacy notes for each known data pattern.
/// </summary>
public static class PatternGuidanceRegistry
{
    private static readonly Dictionary<string, PatternGuidance> Registry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["email"] = new("name@domain.com", "jane@example.com"),
        ["phone"] = new("+1 (555) 123-4567", "+1 (555) 123-4567"),
        ["credit-card"] = new("16 digits, spaces allowed", "4242 4242 4242 4242", "Never stored"),
        ["cvv"] = new("3-4 digits on back of card", "123", "Never stored", ExpectedMinLength: 3, ExpectedMaxLength: 4),
        ["date-partial"] = new("MM/YY", "03/28", ExpectedMinLength: 4, ExpectedMaxLength: 5),
        ["postal-code"] = new("5 digits or ZIP+4", "90210", ExpectedMinLength: 5, ExpectedMaxLength: 10),
        ["password"] = new("8+ characters, mix of letters and numbers", null, ExpectedMinLength: 8),
        ["url"] = new("https://example.com", "https://example.com"),
        ["currency"] = new("Numeric amount, e.g. 29.99", "29.99"),
        ["name"] = new("First and last name", "Jane Smith"),
        ["address"] = new("Street address", "123 Main Street"),
        ["username"] = new("Letters, numbers, underscores", "jane_doe", ExpectedMinLength: 3, ExpectedMaxLength: 32),
        ["search"] = new("Type to search...", null)
    };

    /// <summary>Look up guidance for a pattern name. Returns null if unknown.</summary>
    public static PatternGuidance? Get(string patternName)
        => Registry.GetValueOrDefault(patternName);

    /// <summary>All known pattern names.</summary>
    public static IReadOnlyCollection<string> AllPatterns => Registry.Keys;
}

/// <summary>
///     Format guidance for a single data pattern.
/// </summary>
public sealed record PatternGuidance(
    string FormatHint,
    string? Example,
    string? PrivacyNote = null,
    int? ExpectedMinLength = null,
    int? ExpectedMaxLength = null);

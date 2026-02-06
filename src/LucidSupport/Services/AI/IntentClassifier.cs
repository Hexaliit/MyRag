namespace LucidSupport.Services.AI;

/// <summary>
///     Classifies user intent from a question.
///     Uses LLM when available, falls back to keyword matching.
/// </summary>
public sealed class IntentClassifier(SupportOllamaClient ollamaClient)
{
    private static readonly string[] GreetingKeywords = ["hi", "hello", "hey", "thanks", "thank you", "bye", "goodbye"];
    private static readonly string[] NavigationKeywords = ["where", "find", "navigate", "go to", "how do i get to", "link"];
    private static readonly string[] TroubleshootKeywords = ["not working", "broken", "error", "fail", "can't", "cannot", "stuck", "issue", "problem", "wrong"];

    /// <summary>Classify the intent of a user question.</summary>
    public async Task<SupportIntent> ClassifyAsync(string question, CancellationToken ct = default)
    {
        // Try keyword-based classification first (fast, no LLM needed)
        var keywordResult = ClassifyByKeywords(question);
        if (keywordResult != SupportIntent.Unknown)
            return keywordResult;

        // Try LLM classification
        var llmResult = await ClassifyWithLlmAsync(question, ct);
        return llmResult ?? SupportIntent.GeneralQuestion;
    }

    /// <summary>Fast keyword-based classification.</summary>
    public static SupportIntent ClassifyByKeywords(string question)
    {
        var q = question.ToLowerInvariant().Trim();

        // Greetings (very short messages)
        if (q.Split(' ').Length <= 3 && GreetingKeywords.Any(k => q.Contains(k)))
            return SupportIntent.Greeting;

        // Navigation
        if (NavigationKeywords.Any(k => q.Contains(k)))
            return SupportIntent.Navigation;

        // Troubleshooting
        if (TroubleshootKeywords.Any(k => q.Contains(k)))
            return SupportIntent.Troubleshoot;

        // Field-specific help (mentions a field type or input)
        if (q.Contains("field") || q.Contains("enter") || q.Contains("type") || q.Contains("fill")
            || q.Contains("input") || q.Contains("format"))
            return SupportIntent.FieldHelp;

        return SupportIntent.Unknown;
    }

    private async Task<SupportIntent?> ClassifyWithLlmAsync(string question, CancellationToken ct)
    {
        const string systemPrompt = """
            Classify the user's intent. Reply with exactly one word:
            FieldHelp, GeneralQuestion, Navigation, Troubleshoot, Greeting, Unknown
            """;

        var response = await ollamaClient.GenerateAsync(question, systemPrompt, ct);
        if (response is null) return null;

        // Parse the first word from the response
        var word = response.Split([' ', '\n', ',', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (word is null) return null;

        return word.Trim().ToLowerInvariant() switch
        {
            "fieldhelp" => SupportIntent.FieldHelp,
            "generalquestion" => SupportIntent.GeneralQuestion,
            "navigation" => SupportIntent.Navigation,
            "troubleshoot" => SupportIntent.Troubleshoot,
            "greeting" => SupportIntent.Greeting,
            _ => SupportIntent.Unknown
        };
    }
}

/// <summary>User intent categories.</summary>
public enum SupportIntent
{
    FieldHelp,
    GeneralQuestion,
    Navigation,
    Troubleshoot,
    Greeting,
    Unknown
}

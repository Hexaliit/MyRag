using System.Text.Json;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
///     Result from the conversation sentinel's query resolution.
/// </summary>
public record ConversationSentinelResult
{
    public required string ResolvedQuery { get; init; }
    public bool IsFollowUp { get; init; }
    public float Confidence { get; init; }
    public string? Reason { get; init; }
    public bool ShouldSearch { get; init; } = true;

    /// <summary>
    ///     Personal context resolved from user's personal corpus entities.
    ///     Injected into synthesis prompt as USER_CONTEXT. Null if no personal context applied.
    /// </summary>
    public string? PersonalContext { get; init; }
}

/// <summary>
///     Lightweight sentinel that runs before retrieval to decide whether a question
///     is a follow-up, rewrite it into a standalone query, and score cached segments
///     for salience.
///     Two-tier strategy:
///     Tier 1 (rule-based, no LLM, less than 5ms): pronoun/continuation detection + embedding similarity
///     Tier 2 (LLM-assisted, ~200ms): fires only when Tier 1 detects a follow-up
/// </summary>
public sealed partial class ConversationSentinel
{
    // Pronouns that often refer to previous topic
    private static readonly HashSet<string> Pronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "its", "that", "this", "they", "them", "their", "those", "these",
        "he", "she", "his", "her"
    };

    // Continuation markers that signal a follow-up
    private static readonly string[] ContinuationMarkers =
    [
        "tell me more", "go on", "continue", "explain further", "elaborate",
        "what about", "how about", "and what", "what else", "more about",
        "can you expand", "give me more", "keep going", "more detail",
        "why is that", "how so", "in what way"
    ];

    private readonly IEmbeddingService _embedding;
    private readonly OllamaService? _ollama;
    private readonly bool _ollamaAvailable;

    public ConversationSentinel(
        OllamaService? ollama,
        IEmbeddingService embedding,
        bool ollamaAvailable)
    {
        _ollama = ollama;
        _embedding = embedding;
        _ollamaAvailable = ollamaAvailable;
    }

    /// <summary>
    ///     Resolve a question in context of conversation history.
    ///     Returns a standalone query suitable for retrieval, plus salience metadata.
    /// </summary>
    public async Task<ConversationSentinelResult> ResolveAsync(
        string question,
        List<(string question, string answer, List<string> sourceIds)> history,
        CancellationToken ct)
    {
        // No history = first turn, return as-is
        if (history.Count == 0)
            return new ConversationSentinelResult
            {
                ResolvedQuery = question,
                IsFollowUp = false,
                Confidence = 1.0f,
                Reason = "First turn"
            };

        // --- Tier 1: Rule-based analysis ---
        var tier1 = await AnalyzeTier1Async(question, history, ct);

        if (!tier1.IsFollowUp)
            return new ConversationSentinelResult
            {
                ResolvedQuery = question,
                IsFollowUp = false,
                Confidence = tier1.Confidence,
                Reason = tier1.Reason,
                ShouldSearch = true
            };

        // --- Tier 2: LLM-assisted rewrite (only if Tier 1 detected follow-up) ---
        var resolvedQuery = tier1.RuleBasedRewrite ?? question;

        // Skip LLM when the rule-based rewrite is already complete
        // (e.g., generic continuations like "go on" → "More details about {topic}")
        if (_ollamaAvailable && _ollama != null && !tier1.RewriteComplete)
            try
            {
                var llmResult = await RewriteWithLlmAsync(question, history, ct);
                // Accept if genuinely different and not over-elaborated
                if (llmResult != null
                    && !llmResult.Equals(question, StringComparison.OrdinalIgnoreCase)
                    && llmResult.Length <= Math.Max(question.Length * 2, 120))
                    resolvedQuery = llmResult;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Conversation sentinel LLM rewrite failed: {ex.Message}");
            }

        return new ConversationSentinelResult
        {
            ResolvedQuery = resolvedQuery,
            IsFollowUp = true,
            Confidence = tier1.Confidence,
            Reason = tier1.Reason,
            ShouldSearch = true
        };
    }

    private async Task<Tier1Result> AnalyzeTier1Async(
        string question,
        List<(string question, string answer, List<string> sourceIds)> history,
        CancellationToken ct)
    {
        var lastTurn = history[^1];
        var questionLower = question.ToLowerInvariant().Trim();

        // Check continuation markers
        foreach (var marker in ContinuationMarkers)
            if (questionLower.StartsWith(marker) || questionLower.Contains(marker))
            {
                // Generic continuation (question IS the marker, e.g., "go on", "continue")
                // → rule-based rewrite is complete, LLM should NOT override
                // Specific continuation (has extra terms, e.g., "tell me more about GPD")
                // → LLM may improve the rewrite
                var isGeneric = questionLower.TrimEnd('?', '.', '!').Trim() == marker
                                || questionLower.TrimEnd('?', '.', '!').Trim().Length <= marker.Length + 3;

                return new Tier1Result
                {
                    IsFollowUp = true,
                    Confidence = 0.9f,
                    Reason = $"Continuation marker: '{marker}'",
                    RuleBasedRewrite = BuildContinuationRewrite(question, lastTurn.question, lastTurn.answer, history),
                    RewriteComplete = isGeneric
                };
            }

        // Check for pronoun references
        var words = Regex.Split(questionLower, @"\W+").Where(w => w.Length > 0).ToHashSet();
        var pronounsFound = words.Intersect(Pronouns).ToList();
        if (pronounsFound.Count > 0)
        {
            // Extract topic from previous turn for rewrite
            var topic = ExtractTopic(lastTurn.question, lastTurn.answer);
            var rewrite = ReplacePronounsWithTopic(question, topic);

            return new Tier1Result
            {
                IsFollowUp = true,
                Confidence = 0.8f,
                Reason = $"Pronoun reference: {string.Join(", ", pronounsFound)}",
                RuleBasedRewrite = rewrite
            };
        }

        // Semantic similarity between current and previous question
        try
        {
            var currentEmb = await _embedding.EmbedAsync(question, ct);
            var prevEmb = await _embedding.EmbedAsync(lastTurn.question, ct);
            var similarity = VectorMath.CosineSimilarity(currentEmb, prevEmb);

            if (similarity > 0.7f)
                return new Tier1Result
                {
                    IsFollowUp = true,
                    Confidence = similarity,
                    Reason = $"High semantic similarity: {similarity:F2}",
                    RuleBasedRewrite = $"{question} (in the context of: {lastTurn.question})"
                };

            if (similarity > 0.4f)
                // Related but not a clear follow-up - treat as semi-followup
                // Still search fresh but include relevant cached segments
                return new Tier1Result
                {
                    IsFollowUp = false,
                    Confidence = similarity,
                    Reason = $"Moderate similarity: {similarity:F2}, treating as new query"
                };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Conversation sentinel embedding failed: {ex.Message}");
        }

        // New topic
        return new Tier1Result
        {
            IsFollowUp = false,
            Confidence = 0.9f,
            Reason = "No follow-up signals detected"
        };
    }

    private async Task<string?> RewriteWithLlmAsync(
        string question,
        List<(string question, string answer, List<string> sourceIds)> history,
        CancellationToken ct)
    {
        // Build compact history (last 3 turns)
        var historyBlock = string.Join("\n", history.TakeLast(3).Select(h =>
        {
            var truncA = h.answer.Length > 300 ? h.answer[..300] + "..." : h.answer;
            return $"Q: {h.question}\nA: {truncA}";
        }));

        var prompt = $$"""
                       Rewrite the NEW QUESTION as a short standalone search query by resolving pronouns.

                       HISTORY:
                       {{historyBlock}}

                       NEW QUESTION: "{{question}}"

                       Output JSON:
                       {
                         "resolved_query": "short standalone query"
                       }

                       RULES:
                       - Replace pronouns (it, they, that, etc.) with the specific subject from history
                       - Keep the rewrite SHORT — same length or shorter than the original question
                       - Do NOT add explanations, clauses, or background context
                       - Do NOT restate what the subject is — just name it
                       - Example: "How does it work?" → "How does Bazzite work?" (NOT "How does Bazzite, a Linux gaming distribution, work?")
                       """;

        if (_ollama == null) return null;
        var response = await _ollama.SentinelGenerateJsonAsync(prompt, temperature: 0.1, ct: ct);

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response[jsonStart..(jsonEnd + 1)];
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("resolved_query", out var rq))
                {
                    var resolved = rq.GetString();
                    if (!string.IsNullOrWhiteSpace(resolved))
                        return resolved;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Conversation sentinel JSON parse failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    ///     Extract the main topic from a Q+A turn for pronoun resolution.
    /// </summary>
    private static string ExtractTopic(string prevQuestion, string prevAnswer)
    {
        // Use the previous question as the topic source - it's usually more concise
        // Strip common question prefixes to isolate the subject
        var q = prevQuestion.Trim();
        var prefixes = new[]
        {
            // Standard question prefixes
            "what is ", "what are ", "what was ", "what were ",
            "who is ", "who are ", "who was ",
            "how does ", "how do ", "how is ", "how are ", "how did ",
            "why is ", "why are ", "why does ", "why do ", "why did ",
            "where is ", "where are ", "where does ",
            "when is ", "when does ", "when did ",
            "what does ", "what do ",
            // Instruction-style prefixes
            "can you tell me more about ", "tell me more about ",
            "can you tell me about ", "tell me about ",
            "can you explain ", "explain ",
            "describe ", "define ",
            // Continuation-style prefixes (from follow-up rewrites)
            "more details about ", "more about ",
            "what about ", "how about "
        };

        foreach (var prefix in prefixes)
            if (q.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                q = q[prefix.Length..].TrimEnd('?', '.', '!');
                // Cap at a reasonable length for substitution
                var topicWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (topicWords.Length > 8)
                    q = string.Join(" ", topicWords.Take(8));
                return q;
            }

        // Fallback: take the first noun phrase (up to 5 words, skip leading non-topic words)
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var skipWords = StopwordLists.Stopwords;
        var meaningful = words.SkipWhile(w => skipWords.Contains(w)).ToArray();
        if (meaningful.Length > 0)
            return string.Join(" ", meaningful.Take(5)).TrimEnd('?', '.', '!');

        return string.Join(" ", words.Take(5)).TrimEnd('?', '.', '!');
    }

    /// <summary>
    ///     Walk back through history to find a clean topic (no unresolved pronouns).
    ///     Tries: previous question → earlier questions → answer-based extraction.
    /// </summary>
    private static string ExtractCleanTopic(
        string prevQuestion, string prevAnswer,
        List<(string question, string answer, List<string> sourceIds)>? fullHistory)
    {
        // Try the immediate previous question
        var topic = ExtractTopic(prevQuestion, prevAnswer);
        if (!PronounPattern().IsMatch(topic))
            return topic;

        // Walk back through history to find a question with a clean topic
        if (fullHistory != null)
            for (var i = fullHistory.Count - 2; i >= 0; i--)
            {
                topic = ExtractTopic(fullHistory[i].question, fullHistory[i].answer);
                if (!PronounPattern().IsMatch(topic))
                    return topic;
            }

        // Last resort: extract proper nouns from the answer
        return ExtractTopicFromAnswer(prevAnswer);
    }

    /// <summary>
    ///     Extract the main topic from an answer by finding the first capitalized
    ///     noun phrase (proper nouns are usually the key subject).
    /// </summary>
    private static string ExtractTopicFromAnswer(string answer)
    {
        // Take the first sentence, stripping quoted text (which often echoes the question)
        var cleaned = Regex.Replace(answer, @"""[^""]*""", "");
        cleaned = Regex.Replace(cleaned, @"'[^']*'", "");
        var firstSentence = cleaned.Split(new[] { '.', '!', '?' }, 2)[0].Trim();
        if (firstSentence.Length == 0) return "the previous topic";

        // Find capitalized words that aren't at the start of the sentence
        // (likely proper nouns = the topic)
        var words = firstSentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var properNouns = new List<string>();
        for (var i = 1; i < words.Length; i++)
        {
            var w = words[i].TrimEnd(',', ';', ':');
            if (w.Length > 1 && char.IsUpper(w[0]) && !IsCommonWord(w))
                properNouns.Add(w);
            else if (properNouns.Count > 0)
                break; // End of proper noun phrase
        }

        if (properNouns.Count > 0)
            return string.Join(" ", properNouns);

        // Fallback: first 5 meaningful words from the answer
        var meaningful = words.Where(w => !StopwordLists.Stopwords.Contains(w)).Take(5).ToArray();
        return meaningful.Length > 0
            ? string.Join(" ", meaningful)
            : "the previous topic";
    }

    private static bool IsCommonWord(string word)
    {
        return word is "The" or "A" or "An" or "This" or "That" or "It"
            or "They" or "He" or "She" or "We" or "I"
            or "However" or "Furthermore" or "According" or "Additionally"
            or "Specifically" or "Notably" or "Ultimately";
    }

    private static string ReplacePronounsWithTopic(string question, string topic)
    {
        return PronounPattern().Replace(question, topic);
    }

    [GeneratedRegex(@"\b(?:it|its|that|this|they|them|their|those|these|he|she|his|her)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PronounPattern();

    private static string BuildContinuationRewrite(
        string question, string prevQuestion, string prevAnswer,
        List<(string question, string answer, List<string> sourceIds)>? fullHistory = null)
    {
        // Try to find a clean topic by walking back through history
        var topic = ExtractCleanTopic(prevQuestion, prevAnswer, fullHistory);
        var lower = question.ToLowerInvariant().Trim();

        // If the user already mentions specific terms beyond the marker, keep them
        // e.g., "Tell me more about the GPD controversy" -> preserve "GPD controversy"
        // Only strip to bare topic when the continuation is generic ("tell me more", "go on")

        // Generic continuations with no specific qualifier
        if (lower is "tell me more" or "go on" or "continue" or "elaborate"
            or "keep going" or "more detail" or "more details"
            or "explain further" or "can you expand")
            return $"More details about {topic}";

        // "What about Y" / "How about Y" -> "Y in relation to {topic}"
        if (lower.StartsWith("what about") || lower.StartsWith("how about"))
            return $"{question} in relation to {topic}";

        // User included specific terms — keep the full question with context appended
        // e.g., "Tell me more about the GPD controversy" stays mostly intact
        return $"{question} (regarding {topic})";
    }

    /// <summary>
    ///     Resolve implicit references using personal corpus entities.
    ///     "here" → user's LOC, "my company" → user's ORG, etc.
    /// </summary>
    public async Task<(string resolvedQuery, string? personalContext)>
        ResolveFromPersonalAsync(
            string question,
            PersonalCorpusService personalCorpus,
            CancellationToken ct)
    {
        var resolved = question;
        string? context = null;

        // Implicit location: "here", "locally", "nearby", "my area", "my city"
        if (HasLocationGap(question))
        {
            var locs = await personalCorpus.GetPersonalEntitiesAsync(
                "default", "LOC", 1, ct);
            if (locs.Count > 0)
            {
                resolved = ResolveLocationGap(resolved, locs[0].name);
                context = $"User is in {locs[0].name}";
            }
        }

        // Implicit org: "my company", "my team", "my org", "at work", "my employer"
        if (HasOrgGap(question))
        {
            var orgs = await personalCorpus.GetPersonalEntitiesAsync(
                "default", "ORG", 1, ct);
            if (orgs.Count > 0)
            {
                resolved = ResolveOrgGap(resolved, orgs[0].name);
                context = (context != null ? context + "; " : "")
                          + $"Works at {orgs[0].name}";
            }
        }

        // Implicit tech: "my stack", "my project", "my codebase", "my setup"
        if (HasTechGap(question))
        {
            var techs = await personalCorpus.GetPersonalEntitiesAsync(
                "default", "MISC", 3, ct);
            if (techs.Count > 0)
            {
                var techNames = string.Join(", ", techs.Select(t => t.name));
                context = (context != null ? context + "; " : "")
                          + $"Uses {techNames}";
            }
        }

        return (resolved, context);
    }

    // --- Gap detection helpers ---

    private static bool HasLocationGap(string q)
    {
        return q.Contains("here", StringComparison.OrdinalIgnoreCase)
               || q.Contains("locally", StringComparison.OrdinalIgnoreCase)
               || q.Contains("nearby", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my area", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my city", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOrgGap(string q)
    {
        return q.Contains("my company", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my org", StringComparison.OrdinalIgnoreCase)
               || q.Contains("at work", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my team", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my employer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTechGap(string q)
    {
        return q.Contains("my stack", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my project", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my codebase", StringComparison.OrdinalIgnoreCase)
               || q.Contains("my setup", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLocationGap(string query, string location)
    {
        // Replace "here" and similar with the actual location
        var result = HerePattern().Replace(query, location);
        result = LocallyPattern().Replace(result, $"in {location}");
        result = NearbyPattern().Replace(result, $"near {location}");
        result = result.Replace("my area", location, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("my city", location, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    [GeneratedRegex(@"\bhere\b", RegexOptions.IgnoreCase)]
    private static partial Regex HerePattern();

    [GeneratedRegex(@"\blocally\b", RegexOptions.IgnoreCase)]
    private static partial Regex LocallyPattern();

    [GeneratedRegex(@"\bnearby\b", RegexOptions.IgnoreCase)]
    private static partial Regex NearbyPattern();

    private static string ResolveOrgGap(string query, string org)
    {
        var result = query.Replace("my company", org, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("my org", org, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("my employer", org, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("my team", $"{org} team", StringComparison.OrdinalIgnoreCase);
        result = result.Replace("at work", $"at {org}", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private record Tier1Result
    {
        public bool IsFollowUp { get; init; }
        public float Confidence { get; init; }
        public string? Reason { get; init; }
        public string? RuleBasedRewrite { get; init; }

        /// <summary>
        ///     When true, the rule-based rewrite is complete and the LLM should NOT override it.
        ///     Used for generic continuations like "go on" where Tier 1 already produced a good query.
        /// </summary>
        public bool RewriteComplete { get; init; }
    }
}
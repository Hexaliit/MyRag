using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Interprets natural language prompts into actionable fetch commands
/// Uses a fast "sentinel" LLM for quick triage
/// </summary>
public class PromptInterpreter
{
    private readonly OllamaService _ollama;

    public PromptInterpreter(OllamaService ollama)
    {
        _ollama = ollama;
    }

    /// <summary>
    /// Interpret a natural language prompt into fetch actions
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, CancellationToken ct = default)
    {
        // If Ollama isn't available, use keyword-based fallback
        if (!await _ollama.IsAvailableAsync())
        {
            return FallbackInterpret(prompt);
        }

        var systemPrompt = """
            You are a query interpreter for a news aggregation tool. Parse the user's request and output JSON.

            Output format (JSON only, no markdown):
            {
                "sources": ["hn", "reddit", "so", "bbc", "search:query terms"],
                "vibe": "neutral",
                "searchQueries": ["optional search terms"],
                "websites": ["https://example.com"],
                "limit": 20,
                "topics": ["optional topic filters"]
            }

            Source types:
            - "hn" = Hacker News
            - "reddit" = Reddit programming subreddits
            - "reddit:subreddit" = Specific subreddit (e.g., "reddit:dotnet")
            - "reddit:subreddit:query" = Search within subreddit (e.g., "reddit:csharp:async await")
            - "so" = StackOverflow hot questions
            - "so:tag" = StackOverflow by tag (e.g., "so:csharp", "so:python")
            - "so:search:query" = StackOverflow search
            - "bbc", "guardian", "ars", "verge", "wired", "techcrunch" = News sources
            - "bbc:query" = News source filtered by topic (e.g., "bbc:AI")
            - "lobsters", "devto", "hackernoon" = Tech blogs
            - "search:query" = DuckDuckGo search
            - Direct URLs for specific websites

            Vibes: "doom" (negative focus), "hopeful" (positive), "snarky" (witty), "neutral" (balanced)

            Examples:
            - "summarize tech news" -> {"sources": ["hn", "reddit"], "vibe": "neutral", "limit": 20}
            - "what's happening on bbc and the guardian" -> {"sources": ["bbc", "guardian"], "vibe": "neutral"}
            - "see what bbc says about AI" -> {"sources": ["bbc:AI"], "vibe": "neutral"}
            - "doom scroll hacker news" -> {"sources": ["hn"], "vibe": "doom", "limit": 30}
            - "stackoverflow questions about async await" -> {"sources": ["so:search:async await"], "vibe": "neutral"}
            - "what are c# devs talking about on reddit" -> {"sources": ["reddit:csharp"], "vibe": "neutral"}
            - "snarky summary of AI news" -> {"sources": ["search:AI artificial intelligence news"], "vibe": "snarky"}
            - "lobsters and hackernoon tech news" -> {"sources": ["lobsters", "hackernoon"], "vibe": "neutral"}
            """;

        var userPrompt = $"Parse this request: \"{prompt}\"";

        try
        {
            var response = await _ollama.GenerateAsync(userPrompt, systemPrompt, 0.1, ct);

            // Extract JSON from response
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response[jsonStart..(jsonEnd + 1)];
                var parsed = JsonSerializer.Deserialize(json, PromptJsonContext.Default.ParsedPrompt);
                if (parsed != null)
                {
                    return new InterpretedPrompt
                    {
                        Sources = parsed.Sources ?? ["hn", "reddit"],
                        Vibe = parsed.Vibe ?? "neutral",
                        SearchQueries = parsed.SearchQueries ?? [],
                        Websites = parsed.Websites ?? [],
                        Limit = parsed.Limit > 0 ? parsed.Limit : 20,
                        Topics = parsed.Topics ?? [],
                        RawPrompt = prompt
                    };
                }
            }
        }
        catch
        {
            // Fall back to keyword interpretation
        }

        return FallbackInterpret(prompt);
    }

    /// <summary>
    /// Keyword-based fallback when LLM isn't available
    /// </summary>
    private static InterpretedPrompt FallbackInterpret(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var result = new InterpretedPrompt
        {
            RawPrompt = prompt,
            Sources = [],
            Vibe = "neutral",
            Limit = 20
        };

        // Detect vibe
        if (lower.Contains("doom") || lower.Contains("pessimist") || lower.Contains("negative"))
            result.Vibe = "doom";
        else if (lower.Contains("hopeful") || lower.Contains("positive") || lower.Contains("optimist"))
            result.Vibe = "hopeful";
        else if (lower.Contains("snark") || lower.Contains("witty") || lower.Contains("funny"))
            result.Vibe = "snarky";

        // Detect image queries: "show me an image for...", "image of...", "picture of..."
        var imagePatterns = new[]
        {
            "show me an image for ",
            "show me an image of ",
            "show me a picture of ",
            "show image for ",
            "find an image for ",
            "find an image of ",
            "image of ",
            "picture of ",
            "show image of "
        };

        foreach (var pattern in imagePatterns)
        {
            if (lower.Contains(pattern))
            {
                result.ShowImage = true;
                var idx = lower.IndexOf(pattern);
                var imageQuery = prompt[(idx + pattern.Length)..].Trim();
                // Clean up the query
                var endIdx = imageQuery.IndexOfAny(['.', ',', '!', '?']);
                if (endIdx > 0) imageQuery = imageQuery[..endIdx];
                result.ImageQuery = imageQuery.Trim();

                // Also add as search query
                if (!string.IsNullOrEmpty(result.ImageQuery))
                {
                    result.SearchQueries.Add(result.ImageQuery);
                    result.Topics.Add(result.ImageQuery);
                }
                break;
            }
        }

        // Detect known categories (AI, security, dotnet, etc.)
        foreach (var (category, sources) in CategorySources)
        {
            if (lower.Contains(category.ToLowerInvariant()))
            {
                result.Sources.AddRange(sources);
                result.Topics.Add(category);
            }
        }

        // Detect sources
        if (lower.Contains("hacker news") || lower.Contains("hackernews"))
            result.Sources.Add("hn");
        else if (lower.Contains(" hn ") || lower.StartsWith("hn ") || lower.EndsWith(" hn"))
            result.Sources.Add("hn");

        if (lower.Contains("reddit"))
        {
            // Check for specific subreddit: "r/csharp", "reddit csharp", etc.
            var subredditMatch = System.Text.RegularExpressions.Regex.Match(lower, @"r/(\w+)|reddit[:\s]+(\w+)");
            if (subredditMatch.Success)
            {
                var sub = subredditMatch.Groups[1].Success ? subredditMatch.Groups[1].Value : subredditMatch.Groups[2].Value;
                result.Sources.Add($"reddit:{sub}");
            }
            else
            {
                result.Sources.Add("reddit");
            }
        }

        // StackOverflow
        if (lower.Contains("stackoverflow") || lower.Contains("stack overflow") || lower.Contains(" so "))
        {
            // Check for tag: "so c#", "stackoverflow python"
            var soTagMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(?:stackoverflow|stack overflow|so)[:\s]+(\w+(?:#|\+\+)?)");
            if (soTagMatch.Success)
            {
                result.Sources.Add($"so:{soTagMatch.Groups[1].Value}");
            }
            else
            {
                result.Sources.Add("so");
            }
        }

        // Detect news sources (use source name, not URL - we handle it in ScrollCommand)
        var newsSources = new[]
        {
            "bbc", "guardian", "ars", "verge", "wired", "techcrunch",
            "lobsters", "devto", "hackernoon", "slashdot",
            "cnn", "reuters", "arstechnica", "engadget", "zdnet", "thenextweb",
            "mostlylucid", "medium", "substack"
        };
        foreach (var source in newsSources)
        {
            if (lower.Contains(source) && !result.Sources.Contains(source))
            {
                result.Sources.Add(source);

                // Extract topic terms and also add as search query for better coverage
                var topicTerms = ExtractTopicTermsExcluding(prompt, [source]);
                if (!string.IsNullOrEmpty(topicTerms) && !result.SearchQueries.Contains(topicTerms))
                {
                    result.SearchQueries.Add(topicTerms);
                    if (!result.Topics.Contains(topicTerms))
                        result.Topics.Add(topicTerms);
                }
            }
        }

        // If "search" or "find" with a topic, add search query
        if ((lower.Contains("search") || lower.Contains("find") || lower.Contains("about")) &&
            !result.Sources.Any() && !result.Websites.Any())
        {
            // Extract likely search terms - words after "about", "for", "search"
            var searchTerms = ExtractSearchTerms(prompt);
            if (!string.IsNullOrEmpty(searchTerms))
            {
                result.SearchQueries.Add(searchTerms);
            }
        }

        // If nothing was detected, treat the prompt as a topic search
        if (!result.Sources.Any() && !result.Websites.Any() && !result.SearchQueries.Any())
        {
            // Extract meaningful words from the prompt (skip common words)
            var topicTerms = ExtractTopicTerms(prompt);
            if (!string.IsNullOrEmpty(topicTerms))
            {
                // Add as topic filter and DuckDuckGo search (always include search for best coverage)
                result.Topics.Add(topicTerms);
                result.SearchQueries.Add(topicTerms);

                // Add filterable sources with topic - these support query filtering
                result.Sources.AddRange([
                    $"bbc:{topicTerms}",
                    $"guardian:{topicTerms}",
                    "hn",  // HN doesn't support filtering, but we'll filter post-fetch
                    "reddit",
                    "lobsters",
                    "devto"
                ]);
            }
            else
            {
                // Pure default - no topic, just general tech news
                result.Sources.AddRange(["hn", "reddit", "bbc", "guardian", "ars", "mostlylucid"]);
            }
        }

        return result;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "must", "shall", "can", "need", "dare",
        "about", "for", "with", "what", "how", "why", "when", "where", "who",
        "show", "me", "tell", "give", "get", "find", "search", "scroll",
        "summarize", "summary", "news", "latest", "recent", "today", "now", "on"
    };

    // Common tech categories for smart source selection
    private static readonly Dictionary<string, string[]> CategorySources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = ["hn", "bbc:AI", "guardian:AI", "verge:AI", "search:AI"],
        ["llm"] = ["hn", "search:LLM", "devto:llm"],
        ["machine learning"] = ["hn", "search:machine learning", "devto:machine-learning"],
        ["security"] = ["hn", "ars", "search:cybersecurity"],
        ["dotnet"] = ["reddit:dotnet", "reddit:csharp", "devto:dotnet", "mostlylucid"],
        ["csharp"] = ["reddit:csharp", "so:csharp", "devto:csharp", "mostlylucid"],
        ["c#"] = ["reddit:csharp", "so:csharp", "devto:csharp", "mostlylucid"],
        ["python"] = ["reddit:python", "so:python", "devto:python", "hn"],
        ["rust"] = ["reddit:rust", "lobsters", "hn", "devto:rust"],
        ["javascript"] = ["reddit:javascript", "devto:javascript", "hn"],
        ["web"] = ["verge", "techcrunch", "hn", "devto:webdev"],
        ["cloud"] = ["hn", "techcrunch", "ars", "devto:cloud"],
        ["startup"] = ["hn", "techcrunch", "reddit:startups"],
        ["rag"] = ["hn", "devto", "mostlylucid", "search:RAG retrieval"],
    };

    private static string ExtractTopicTerms(string prompt) =>
        ExtractTopicTermsExcluding(prompt, []);

    private static string ExtractTopicTermsExcluding(string prompt, IEnumerable<string> exclude)
    {
        var excludeSet = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);

        var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w) && !excludeSet.Contains(w) && w.Length > 1)
            .ToList();

        return words.Count > 0 ? string.Join(" ", words) : "";
    }

    private static string ExtractSearchTerms(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var markers = new[] { "about ", "for ", "search ", "find ", "regarding " };

        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker);
            if (idx >= 0)
            {
                var rest = prompt[(idx + marker.Length)..].Trim();
                // Take up to the next punctuation or common stop word
                var endIdx = rest.IndexOfAny(['.', ',', '!', '?']);
                if (endIdx > 0) rest = rest[..endIdx];
                return rest.Trim();
            }
        }

        return prompt;
    }
}

public record InterpretedPrompt
{
    public required string RawPrompt { get; init; }
    public List<string> Sources { get; set; } = [];
    public string Vibe { get; set; } = "neutral";
    public List<string> SearchQueries { get; set; } = [];
    public List<string> Websites { get; set; } = [];
    public int Limit { get; set; } = 20;
    public List<string> Topics { get; set; } = [];

    /// <summary>
    /// True if user wants to see an image (e.g., "show me an image for...")
    /// </summary>
    public bool ShowImage { get; set; }

    /// <summary>
    /// Specific image query extracted from prompt
    /// </summary>
    public string? ImageQuery { get; set; }
}

public record ParsedPrompt
{
    [JsonPropertyName("sources")] public List<string>? Sources { get; init; }
    [JsonPropertyName("vibe")] public string? Vibe { get; init; }
    [JsonPropertyName("searchQueries")] public List<string>? SearchQueries { get; init; }
    [JsonPropertyName("websites")] public List<string>? Websites { get; init; }
    [JsonPropertyName("limit")] public int Limit { get; init; }
    [JsonPropertyName("topics")] public List<string>? Topics { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ParsedPrompt))]
public partial class PromptJsonContext : JsonSerializerContext;

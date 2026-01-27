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
    private readonly EmbeddingService? _embedding;

    public PromptInterpreter(OllamaService ollama, EmbeddingService? embedding = null)
    {
        _ollama = ollama;
        _embedding = embedding;
    }

    /// <summary>
    /// Interpret a natural language prompt into fetch actions.
    /// Overload without NER context — delegates to the full version.
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, CancellationToken ct = default)
        => await InterpretAsync(prompt, nerContext: null, ct);

    /// <summary>
    /// Interpret a natural language prompt into fetch actions.
    /// Uses a structured sentinel LLM that outputs category weights and intent type
    /// instead of picking specific sources — source selection is heuristic.
    /// When NER context is provided, entity-specific search queries are injected
    /// for more precise API filtering.
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, QueryNerContext? nerContext, CancellationToken ct = default)
    {
        // If Ollama isn't available, use keyword-based fallback
        if (!await _ollama.IsAvailableAsync())
        {
            var fallback = FallbackInterpret(prompt);
            if (nerContext != null)
                EnrichWithNerContext(fallback, nerContext);
            return fallback;
        }

        var systemPrompt = BuildStructuredSentinelPrompt(nerContext);

        // Build user prompt — include NER entities when available
        string userPrompt;
        if (nerContext?.HasEntities == true)
        {
            var entityInfo = string.Join(", ", nerContext.Entities
                .Select(e => $"{e.Text} ({e.Type}: {e.Type switch {
                    "PER" => "person",
                    "ORG" => "organization",
                    "LOC" => "location",
                    _ => "entity"
                }})"));
            userPrompt = $"""
                Classify this request: "{prompt}"

                DETECTED ENTITIES: {entityInfo}
                Include entity names in search_queries with quotes for exact matching.
                """;
        }
        else
        {
            userPrompt = $"Classify this request: \"{prompt}\"";
        }

        try
        {
            // Use JSON mode to force valid JSON output from Ollama
            var response = await _ollama.GenerateJsonAsync(userPrompt, systemPrompt, 0.1, ct);

            // With JSON mode, the response should be valid JSON directly
            if (string.IsNullOrWhiteSpace(response))
            {
                Spectre.Console.AnsiConsole.MarkupLine("[yellow]Sentinel returned empty response[/]");
                throw new InvalidOperationException("Empty sentinel response");
            }
            var json = response.Trim();
            if (!json.StartsWith('{'))
            {
                // Fallback: extract JSON from response text
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    json = response[jsonStart..(jsonEnd + 1)];
            }

            if (json.StartsWith('{'))
            {
                // Try structured SentinelIntent first
                SentinelIntent? intent = null;
                try
                {
                    intent = JsonSerializer.Deserialize(json, OllamaJsonContext.Default.SentinelIntent);
                }
                catch (JsonException ex)
                {
                    Spectre.Console.AnsiConsole.MarkupLine(
                        $"[red]Sentinel JSON parse error: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
                    Spectre.Console.AnsiConsole.MarkupLine(
                        $"[grey]Sentinel raw: {Spectre.Console.Markup.Escape(json[..Math.Min(json.Length, 400)])}[/]");
                }

                if (intent != null && (intent.Categories?.Count ?? 0) > 0)
                {
                    var router = GetRouter();
                    var result = SentinelSourceMapper.ToInterpretedPrompt(intent, router, prompt, nerContext);
                    return result;
                }

                // Log why SentinelIntent failed
                if (intent != null)
                    Spectre.Console.AnsiConsole.MarkupLine(
                        $"[yellow]Sentinel: parsed but empty categories. intent={Spectre.Console.Markup.Escape(intent.Intent ?? "null")}, queries={Spectre.Console.Markup.Escape(string.Join(", ", intent.SearchQueries ?? []))}[/]");
                else
                    Spectre.Console.AnsiConsole.MarkupLine(
                        $"[yellow]Sentinel: deserialized null. Raw: {Spectre.Console.Markup.Escape(json[..Math.Min(json.Length, 300)])}[/]");

                // Fallback: try legacy ParsedPrompt format (backward compatibility)
                var parsed = JsonSerializer.Deserialize(json, PromptJsonContext.Default.ParsedPrompt);
                if (parsed != null)
                {
                    var result = new InterpretedPrompt
                    {
                        Sources = parsed.Sources ?? ["hn", "reddit"],
                        Vibe = parsed.Vibe ?? "neutral",
                        SearchQueries = parsed.SearchQueries ?? [],
                        Websites = parsed.Websites ?? [],
                        Limit = parsed.Limit > 0 ? parsed.Limit : 20,
                        Topics = parsed.Topics ?? [],
                        RawPrompt = prompt
                    };

                    EnrichWithYamlRouting(result, prompt);
                    if (nerContext != null)
                        EnrichWithNerContext(result, nerContext);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            var trace = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[red]Sentinel failed: {Spectre.Console.Markup.Escape(ex.GetType().Name)}: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
            if (!string.IsNullOrEmpty(trace))
                Spectre.Console.AnsiConsole.MarkupLine($"[grey]  at {Spectre.Console.Markup.Escape(trace)}[/]");
        }

        var fallbackResult = FallbackInterpret(prompt);
        if (nerContext != null)
            EnrichWithNerContext(fallbackResult, nerContext);
        return fallbackResult;
    }

    /// <summary>
    /// Build the structured sentinel system prompt.
    /// Kept concise for small local models — Ollama JSON mode ensures valid output.
    /// </summary>
    private static string BuildStructuredSentinelPrompt(QueryNerContext? nerContext)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");
        return $"""
            You are a search query optimizer. Given a user's request, output JSON with two main goals:
            1. Craft the best possible search engine queries (for Google News and DuckDuckGo)
            2. Categorize the topic with weights so we know what kind of news sources to check

            JSON fields:
            - "intent": "news" | "roundup" | "research" | "howto" | "qa"
            - "categories": topic weights 0.0-1.0. ONLY use these exact categories: technology, ai, security, programming, science, health, pharma, business, finance, politics, world, entertainment, humor, sports, environment, climate, space, disaster, factcheck. Do NOT invent categories. Do NOT use "news" as a category.
            - "tone": "neutral" | "doom" | "hopeful" | "snarky" | "funny" | "upbeat" | "friendly"
            - "time_sensitivity": "breaking" | "today" | "week" | "any"
            - "search_queries": 2-3 optimized search engine queries. Fix spelling. Expand abbreviations (SNL → Saturday Night Live). Quote entity names. Add time/date context when relevant. Today is {today}. Do NOT include search engine names in the query text.
            - "entities": named entities found in the query
            - "explicit_sources": only if user named a specific source (hn, bbc, reddit, etc.)
            - "limit": number (default 20)

            Only assign categories that match the actual topic. Do NOT default to technology.
            """;
    }

    /// <summary>
    /// Keyword-based fallback when LLM isn't available
    /// </summary>
    private InterpretedPrompt FallbackInterpret(string prompt)
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

        // Use YAML-driven topic routing for category detection
        var router = GetRouter();
        var detectedTopic = router.DetectTopic(prompt);
        if (detectedTopic != "default")
        {
            var routing = router.RouteByTopic(detectedTopic);
            // Map YAML source names to CLI source identifiers
            foreach (var src in routing.Sources)
            {
                var mapped = MapYamlSourceToCliSource(src, routing, prompt);
                if (mapped != null && !result.Sources.Contains(mapped))
                    result.Sources.Add(mapped);
            }
            result.Topics.Add(detectedTopic);
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

        // If nothing was detected, treat the prompt as a topic search with YAML routing
        if (!result.Sources.Any() && !result.Websites.Any() && !result.SearchQueries.Any())
        {
            var topicTerms = ExtractTopicTerms(prompt);
            if (!string.IsNullOrEmpty(topicTerms))
            {
                result.Topics.Add(topicTerms);
                result.SearchQueries.Add(topicTerms);

                // Use SourceRouter to get topic-appropriate sources
                var routing = router.Route(topicTerms);
                foreach (var src in routing.Sources)
                {
                    var mapped = MapYamlSourceToCliSource(src, routing, prompt);
                    if (mapped != null && !result.Sources.Contains(mapped))
                        result.Sources.Add(mapped);
                }

                // Always include Google News search for non-default topics
                if (!result.Sources.Any(s => s.StartsWith("gnews")))
                    result.Sources.Insert(0, $"gnews:{topicTerms}");
            }
            else
            {
                // Pure default - use default routing from YAML
                var defaultRouting = router.RouteByTopic("default");
                foreach (var src in defaultRouting.Sources)
                {
                    var mapped = MapYamlSourceToCliSource(src, defaultRouting, null);
                    if (mapped != null && !result.Sources.Contains(mapped))
                        result.Sources.Add(mapped);
                }
            }
        }

        // Always ensure Google News is included for topic-based queries
        if (result.Topics.Count > 0 && !result.Sources.Any(s => s.StartsWith("gnews")))
        {
            var topicQuery = string.Join(" ", result.Topics);
            result.Sources.Insert(0, $"gnews:{topicQuery}");
        }

        return result;
    }

    /// <summary>
    /// Map a YAML source name (e.g., "google_news", "bbc", "hn") to the CLI source identifier
    /// used by ScrollCommand (e.g., "gnews:query", "bbc:health", "hn").
    /// </summary>
    private static string? MapYamlSourceToCliSource(string yamlSource, RoutingResult routing, string? query)
    {
        return yamlSource switch
        {
            "google_news" => !string.IsNullOrEmpty(query)
                ? $"gnews:{ExtractTopicTerms(query ?? "")}"
                : routing.GoogleNewsTopic != null
                    ? $"gnews_topic:{routing.GoogleNewsTopic}"
                    : "gnews",
            "duckduckgo" => !string.IsNullOrEmpty(query)
                ? $"search:{ExtractTopicTerms(query ?? "")}"
                : null,
            "bbc" => routing.BbcCategory != null
                ? $"bbc:{routing.BbcCategory}"
                : "bbc",
            "guardian" => "guardian",
            "cnn" => "cnn",
            "reuters" => "reuters",
            "hn" => "hn",
            "reddit" => "reddit",
            "ars" => "ars",
            "verge" => "verge",
            "lobsters" => "lobsters",
            "devto" => "devto",
            "techcrunch" => "techcrunch",
            "wired" => "wired",
            "npr" => "npr",
            "theregister" => "theregister",
            "sciencedaily" => "sciencedaily",
            "phys" => "phys",
            "carbonbrief" => "carbonbrief",
            "spaceflight" => "spaceflight",
            "earthquake" => "earthquake",
            "factcheck" => "factcheck",
            "wikipedia" => "wikipedia",
            "arxiv" => !string.IsNullOrEmpty(query)
                ? $"arxiv:{ExtractTopicTerms(query)}"
                : "arxiv",
            "theonion" => "theonion",
            "babylonbee" => "babylonbee",
            _ => yamlSource // Pass through unknown sources
        };
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "must", "shall", "can", "need", "dare",
        "about", "for", "with", "what", "how", "why", "when", "where", "who",
        "show", "me", "tell", "give", "get", "find", "search", "scroll",
        "summarize", "summary", "news", "latest", "recent", "today", "now", "on",
        "new", "any", "some", "all", "current", "happening", "update", "updates"
    };

    /// <summary>
    /// Lazy-loaded source router for YAML-driven topic routing.
    /// Initialized with semantic embeddings when EmbeddingService is available.
    /// </summary>
    private static readonly Lazy<SourceRouter> SharedRouter = new(() => SourceRouter.Load());

    private SourceRouter GetRouter()
    {
        var router = SharedRouter.Value;
        // Initialize semantic embeddings if not yet done and embedding service is available
        if (!router.HasEmbeddings && _embedding != null)
            router.InitializeEmbeddings(_embedding);
        return router;
    }

    /// <summary>
    /// Enrich an interpreted prompt with NER-extracted entity queries.
    /// Only adds precise entity search queries (gnews + search) — does NOT add
    /// news outlet preferences (bbc:business, reuters, etc.) because NER entity type
    /// (ORG/PER/LOC) doesn't determine topic category (e.g., "SNL" is ORG but entertainment).
    /// Topic-based source routing is handled by the sentinel LLM and YAML routing.
    /// </summary>
    private static void EnrichWithNerContext(InterpretedPrompt result, QueryNerContext nerContext)
    {
        if (!nerContext.HasEntities) return;

        foreach (var eq in nerContext.EntityQueries)
        {
            // Add entity-specific quoted queries as search queries
            if (!result.SearchQueries.Any(q =>
                q.Contains(eq.EntityText, StringComparison.OrdinalIgnoreCase)))
            {
                result.SearchQueries.Add(eq.Query);
            }

            // Add entity-specific gnews queries
            var gnewsQuery = $"gnews:{eq.EntityText}";
            if (!result.Sources.Any(s =>
                s.Equals(gnewsQuery, StringComparison.OrdinalIgnoreCase) ||
                (s.StartsWith("gnews:", StringComparison.OrdinalIgnoreCase) &&
                 s.Contains(eq.EntityText, StringComparison.OrdinalIgnoreCase))))
            {
                // Insert after existing gnews sources (don't displace them)
                var lastGnews = result.Sources.FindLastIndex(s =>
                    s.StartsWith("gnews", StringComparison.OrdinalIgnoreCase));
                result.Sources.Insert(lastGnews + 1, gnewsQuery);
            }
        }

        // Store NER context on the prompt for downstream use
        result.NerContext = nerContext;
    }

    /// <summary>
    /// Tech-only sources that should be removed when query isn't about tech.
    /// The legacy sentinel LLM often defaults to tech sources (hn, reddit)
    /// even for entertainment, health, sports queries.
    /// </summary>
    private static readonly HashSet<string> TechOnlySources = new(StringComparer.OrdinalIgnoreCase)
    {
        "hn", "lobsters", "devto", "techcrunch", "wired", "theregister", "ars", "verge"
    };

    /// <summary>
    /// Topics that warrant tech-specific sources.
    /// </summary>
    private static readonly HashSet<string> TechTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "technology", "programming", "ai", "security"
    };

    /// <summary>
    /// Enrich an interpreted prompt with YAML-driven routing.
    /// The LLM often returns sparse or mismatched sources (e.g., tech sources for entertainment).
    /// YAML routing ensures the correct source spread for the detected topic AND removes
    /// tech-only sources when the topic is non-tech.
    /// </summary>
    private void EnrichWithYamlRouting(InterpretedPrompt result, string prompt)
    {
        var router = GetRouter();
        var detectedTopic = router.DetectTopic(prompt);
        if (detectedTopic == "default") return;

        var routing = router.RouteByTopic(detectedTopic, prompt);

        // If detected topic is NOT tech, remove tech-only sources the LLM mistakenly added.
        // This prevents the legacy sentinel from defaulting to hn/reddit for entertainment queries.
        if (!TechTopics.Contains(detectedTopic))
        {
            result.Sources.RemoveAll(s => TechOnlySources.Contains(s.Split(':')[0]));
        }

        // Add YAML-routed sources that aren't already present
        foreach (var src in routing.Sources)
        {
            var mapped = MapYamlSourceToCliSource(src, routing, prompt);
            if (mapped != null && !result.Sources.Any(s =>
                s.Equals(mapped, StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith(mapped.Split(':')[0], StringComparison.OrdinalIgnoreCase)))
            {
                result.Sources.Add(mapped);
            }
        }

        // Add topic if not already present
        if (!result.Topics.Contains(detectedTopic))
            result.Topics.Add(detectedTopic);
    }

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

    /// <summary>
    /// NER-extracted entity context from query preprocessing.
    /// Available when NER model is installed and query contains named entities.
    /// </summary>
    public QueryNerContext? NerContext { get; set; }

    /// <summary>
    /// Structured sentinel intent (when using structured JSON sentinel).
    /// Contains category weights, intent type, tone, time sensitivity.
    /// </summary>
    public SentinelIntent? SentinelIntent { get; set; }
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

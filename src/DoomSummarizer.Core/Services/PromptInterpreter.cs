using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
///     Interprets natural language prompts into actionable fetch commands
///     Uses a fast "sentinel" LLM for quick triage
/// </summary>
public partial class PromptInterpreter
{
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
    ///     Lazy-loaded source router for YAML-driven topic routing.
    ///     Initialized with semantic embeddings when EmbeddingService is available.
    /// </summary>
    private static readonly Lazy<SourceRouter> SharedRouter = new(() => SourceRouter.Load());

    private readonly IEmbeddingService? _embedding;
    private readonly OllamaService _ollama;

    public PromptInterpreter(OllamaService ollama, IEmbeddingService? embedding = null)
    {
        _ollama = ollama;
        _embedding = embedding;
    }

    /// <summary>
    ///     Interpret a natural language prompt into fetch actions.
    ///     Overload without NER context — delegates to the full version.
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, CancellationToken ct = default)
    {
        return await InterpretAsync(prompt, null, ct);
    }

    /// <summary>
    ///     Interpret a natural language prompt into fetch actions.
    ///     Uses a structured sentinel LLM that outputs category weights and intent type
    ///     instead of picking specific sources — source selection is heuristic.
    ///     When NER context is provided, entity-specific search queries are injected
    ///     for more precise API filtering.
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, QueryNerContext? nerContext,
        CancellationToken ct = default)
    {
        // If Ollama isn't available, use keyword-based fallback
        if (!await _ollama.IsAvailableAsync())
        {
            var fallback = await FallbackInterpretAsync(prompt);
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
                Debug.WriteLine("Sentinel returned empty response");
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
                    Debug.WriteLine(
                        $"Sentinel JSON parse error: {ex.Message}");
                    Debug.WriteLine(
                        $"Sentinel raw: {json[..Math.Min(json.Length, 400)]}");
                }

                if (intent != null && (intent.Categories?.Count ?? 0) > 0)
                {
                    // Debug: show composite query detection
                    if (intent.IsComposite || intent.HasSubqueries)
                    {
                        Debug.WriteLine(
                            $"Composite query detected: {intent.Subqueries?.Count ?? 0} subqueries");
                        foreach (var sq in intent.Subqueries ?? [])
                            Debug.WriteLine($"  - {sq}");
                    }

                    var router = await GetRouterAsync();
                    var result = SentinelSourceMapper.ToInterpretedPrompt(intent, router, prompt, nerContext);
                    return result;
                }

                // Log why SentinelIntent failed - show raw JSON for debugging
                if (intent != null)
                {
                    Debug.WriteLine(
                        $"Sentinel: parsed but empty categories. intent={intent.Intent ?? "null"}, queries={string.Join(", ", intent.SearchQueries ?? [])}");
                    Debug.WriteLine(
                        $"Raw JSON: {json[..Math.Min(json.Length, 500)]}");
                }
                else
                {
                    Debug.WriteLine(
                        $"Sentinel: deserialized null. Raw: {json[..Math.Min(json.Length, 300)]}");
                }

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

                    await EnrichWithYamlRoutingAsync(result, prompt);
                    if (nerContext != null)
                        EnrichWithNerContext(result, nerContext);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            var trace = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            Debug.WriteLine(
                $"Sentinel failed: {ex.GetType().Name}: {ex.Message}");
            if (!string.IsNullOrEmpty(trace))
                Debug.WriteLine($"  at {trace}");
        }

        var fallbackResult = await FallbackInterpretAsync(prompt);
        if (nerContext != null)
            EnrichWithNerContext(fallbackResult, nerContext);
        return fallbackResult;
    }

    /// <summary>
    ///     Build the structured sentinel system prompt.
    ///     Simplified for small local models — Ollama JSON mode ensures valid output.
    /// </summary>
    private static string BuildStructuredSentinelPrompt(QueryNerContext? nerContext)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");
        return $$"""
                 Output JSON classifying the search query. Today is {{today}}.

                 REQUIRED fields:
                 {
                   "categories": {"topic": 0.8},  // Use: technology, ai, programming, science, health, business, finance, politics, world, entertainment, sports
                   "intent": "qa",  // news|roundup|research|howto|qa|search_only|trend|comparison
                   "search_queries": ["optimized search query"],  // 2-3 search queries
                   "filter_keywords": ["keyword1", "keyword2"],  // 3-5 key terms
                   "tone": "neutral"  // neutral|doom|hopeful|snarky|funny
                 }

                 COMPOSITE queries (multiple questions joined by "and"/"also"):
                 {
                   "is_composite": true,
                   "subqueries": ["First standalone question?", "Second standalone question?"]
                 }
                 Resolve pronouns in each subquery: "it" → the actual subject.

                 IMPORTANT: Use "qa" for factual/trivia/knowledge questions, NOT "news".
                 "How much can a swallow carry?" → qa (factual, not current events)
                 "What is the speed of light?" → qa (factual knowledge)
                 "What happened at the UN today?" → news (current events)
                 "Latest AI developments" → news (current events)

                 Use "search_only" for queries needing a direct web search answer, NOT news feeds:
                 weather, sports scores, stock prices, time zones, unit conversions, "what is X", definitions.

                 Example: "What happens in Wuthering Heights and when was the latest movie made?"
                 {
                   "categories": {"entertainment": 0.9},
                   "intent": "qa",
                   "search_queries": ["Wuthering Heights plot summary", "Wuthering Heights movie 2024"],
                   "filter_keywords": ["Wuthering Heights", "movie", "plot"],
                   "is_composite": true,
                   "subqueries": ["What is the plot of Wuthering Heights?", "When was the latest Wuthering Heights movie made?"],
                   "tone": "neutral"
                 }
                 """;
    }

    /// <summary>
    ///     Keyword-based fallback when LLM isn't available
    /// </summary>
    private async Task<InterpretedPrompt> FallbackInterpretAsync(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var result = new InterpretedPrompt
        {
            RawPrompt = prompt,
            Sources = [],
            Vibe = "neutral",
            Limit = 20,
            GraphScope = QueryTypeDetector.DetectGraphScope(prompt)
        };

        // Detect vibe
        if (lower.Contains("doom") || lower.Contains("pessimist") || lower.Contains("negative"))
            result.Vibe = "doom";
        else if (lower.Contains("hopeful") || lower.Contains("positive") || lower.Contains("optimist"))
            result.Vibe = "hopeful";
        else if (lower.Contains("snark") || lower.Contains("witty"))
            result.Vibe = "snarky";
        else if (lower.Contains("funny") || lower.Contains("humor") || lower.Contains("comedy"))
            result.Vibe = "funny";
        else if (lower.Contains("upbeat") || lower.Contains("excited") || lower.Contains("enthusiast"))
            result.Vibe = "upbeat";
        else if (lower.Contains("friendly") || lower.Contains("casual") || lower.Contains("chill"))
            result.Vibe = "friendly";
        else if (lower.Contains("toon") || lower.Contains("cartoon") || lower.Contains("comic"))
            result.Vibe = "toon";

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

        // Use YAML-driven topic routing for category detection
        var router = await GetRouterAsync();
        var detectedTopic = await router.DetectTopicAsync(prompt);
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
            var subredditMatch = SubredditPattern().Match(lower);
            if (subredditMatch.Success)
            {
                var sub = subredditMatch.Groups[1].Success
                    ? subredditMatch.Groups[1].Value
                    : subredditMatch.Groups[2].Value;
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
            var soTagMatch = StackOverflowTagPattern().Match(lower);
            if (soTagMatch.Success)
                result.Sources.Add($"so:{soTagMatch.Groups[1].Value}");
            else
                result.Sources.Add("so");
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

        // If "search" or "find" with a topic, add search query
        if ((lower.Contains("search") || lower.Contains("find") || lower.Contains("about")) &&
            !result.Sources.Any() && !result.Websites.Any())
        {
            // Extract likely search terms - words after "about", "for", "search"
            var searchTerms = ExtractSearchTerms(prompt);
            if (!string.IsNullOrEmpty(searchTerms)) result.SearchQueries.Add(searchTerms);
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
                var routing = await router.RouteAsync(topicTerms);
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
    ///     Map a YAML source name (e.g., "google_news", "bbc", "hn") to the CLI source identifier
    ///     used by ScrollCommand (e.g., "gnews:query", "bbc:health", "hn").
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

    private async Task<SourceRouter> GetRouterAsync()
    {
        var router = SharedRouter.Value;
        // Initialize semantic embeddings if not yet done and embedding service is available
        if (!router.HasEmbeddings && _embedding != null)
            await router.InitializeEmbeddingsAsync(_embedding);
        return router;
    }

    /// <summary>
    ///     Enrich an interpreted prompt with NER-extracted entity queries.
    ///     Only adds precise entity search queries (gnews + search) — does NOT add
    ///     news outlet preferences (bbc:business, reuters, etc.) because NER entity type
    ///     (ORG/PER/LOC) doesn't determine topic category (e.g., "SNL" is ORG but entertainment).
    ///     Topic-based source routing is handled by the sentinel LLM and YAML routing.
    /// </summary>
    private static void EnrichWithNerContext(InterpretedPrompt result, QueryNerContext nerContext)
    {
        if (!nerContext.HasEntities) return;

        foreach (var eq in nerContext.EntityQueries)
        {
            // Add entity-specific quoted queries as search queries
            if (!result.SearchQueries.Any(q =>
                    q.Contains(eq.EntityText, StringComparison.OrdinalIgnoreCase)))
                result.SearchQueries.Add(eq.Query);

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
    ///     Enrich an interpreted prompt with YAML-driven routing.
    ///     The LLM often returns sparse or mismatched sources (e.g., tech sources for entertainment).
    ///     YAML routing ensures the correct source spread for the detected topic AND removes
    ///     tech-only sources when the topic is non-tech.
    /// </summary>
    private async Task EnrichWithYamlRoutingAsync(InterpretedPrompt result, string prompt)
    {
        var router = await GetRouterAsync();
        var detectedTopic = await router.DetectTopicAsync(prompt);
        if (detectedTopic == "default") return;

        var routing = router.RouteByTopic(detectedTopic, prompt);

        // If detected topic is NOT tech, remove tech-only sources the LLM mistakenly added.
        // This prevents the legacy sentinel from defaulting to hn/reddit for entertainment queries.
        var techTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "technology", "programming", "ai", "security" };
        if (!techTopics.Contains(detectedTopic))
            result.Sources.RemoveAll(s => router.HasCapability(s.Split(':')[0], "tech_only"));

        // Add YAML-routed sources that aren't already present
        foreach (var src in routing.Sources)
        {
            var mapped = MapYamlSourceToCliSource(src, routing, prompt);
            if (mapped != null && !result.Sources.Any(s =>
                    s.Equals(mapped, StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith(mapped.Split(':')[0], StringComparison.OrdinalIgnoreCase)))
                result.Sources.Add(mapped);
        }

        // Add topic if not already present
        if (!result.Topics.Contains(detectedTopic))
            result.Topics.Add(detectedTopic);
    }

    private static string ExtractTopicTerms(string prompt)
    {
        return ExtractTopicTermsExcluding(prompt, []);
    }

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

    [GeneratedRegex(@"(?:r/|reddit\s+)(\w+)|reddit[:\s]+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex SubredditPattern();

    [GeneratedRegex(@"(?:stackoverflow|stack overflow|so)[:\s]+(\w+(?:#|\+\+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex StackOverflowTagPattern();
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
    ///     True if user wants to see an image (e.g., "show me an image for...")
    /// </summary>
    public bool ShowImage { get; set; }

    /// <summary>
    ///     Specific image query extracted from prompt
    /// </summary>
    public string? ImageQuery { get; set; }

    /// <summary>
    ///     NER-extracted entity context from query preprocessing.
    ///     Available when NER model is installed and query contains named entities.
    /// </summary>
    public QueryNerContext? NerContext { get; set; }

    /// <summary>
    ///     Structured sentinel intent (when using structured JSON sentinel).
    ///     Contains category weights, intent type, tone, time sensitivity.
    /// </summary>
    public SentinelIntent? SentinelIntent { get; set; }

    /// <summary>
    ///     GraphRAG scope: Local (specific), Global (sensemaking), Connective (DRIFT).
    ///     Auto-detected from query patterns and sentinel intent.
    ///     When Global or Connective, entity graph enrichment is auto-enabled.
    /// </summary>
    public GraphScope GraphScope { get; set; } = GraphScope.Local;
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
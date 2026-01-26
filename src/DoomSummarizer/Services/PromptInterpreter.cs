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

            Source types (pick 3-6 diverse sources per query):

            SEARCH ENGINES (query-based, broad coverage):
            - "gnews:query" = Google News search — best first source for ANY topic
            - "gnews_topic:TOPIC" = Google News topic feed (HEALTH, SCIENCE, BUSINESS, TECHNOLOGY, ENTERTAINMENT, SPORTS, WORLD, NATION)
            - "search:query" = DuckDuckGo search — good fallback, web-wide results

            TECH COMMUNITY (developer discussions, open source, startups):
            - "hn" = Hacker News — tech, startups, CS research, Show HN projects
            - "reddit" / "reddit:subreddit" = Reddit — community discussion; use reddit:science, reddit:worldnews, etc. for non-tech
            - "lobsters" = Lobsters — curated tech/CS, higher signal than HN
            - "devto" = Dev.to — developer blogs, tutorials, career advice
            - "so" / "so:tag" / "so:search:query" = StackOverflow — technical Q&A

            NEWS OUTLETS (journalism, current events, analysis):
            - "bbc" / "bbc:category" = BBC News — categories: technology, health, science, business, world, politics, entertainment, environment
            - "guardian" = The Guardian — strong on environment, science, world affairs
            - "cnn" = CNN — breaking news, US focus
            - "reuters" = Reuters — wire service, business, world events, factual
            - "npr" = NPR — US public radio, good on health, science, politics
            - "theregister" = The Register — UK tech journalism, security, enterprise IT

            TECH MEDIA (product launches, industry analysis):
            - "ars" = Ars Technica — deep tech analysis, science coverage
            - "verge" = The Verge — consumer tech, AI, platforms
            - "techcrunch" = TechCrunch — startups, funding, product launches
            - "wired" = Wired — tech culture, longform

            SCIENCE & RESEARCH (papers, preprints, data):
            - "arxiv:query" = arXiv preprints — full paper abstracts, AI/ML/physics/math/bio
            - "sciencedaily" = Science Daily — research news summaries
            - "phys" = Phys.org — science news aggregator
            - "carbonbrief" = Carbon Brief — climate science analysis
            - "spaceflight" = Spaceflight News API — launches, NASA, ESA, SpaceX

            SPECIALIZED APIs (structured data, fact-checking):
            - "factcheck" = Fact-checkers (Snopes, PolitiFact, FullFact)
            - "earthquake" = USGS seismic data (real-time earthquakes)
            - "wikipedia" = Wikipedia current events, featured articles

            Direct URLs for specific websites also work.

            STRATEGY: Always include gnews:query for non-tech topics. For research/academic queries, include arxiv. Mix news outlets + community + search for diversity.

            Vibes: "doom" (negative focus), "hopeful" (positive), "snarky" (witty), "neutral" (balanced)

            Examples:
            - "summarize tech news" -> {"sources": ["hn", "reddit", "verge", "techcrunch"], "vibe": "neutral", "limit": 20}
            - "what's happening on bbc and the guardian" -> {"sources": ["bbc", "guardian"], "vibe": "neutral"}
            - "doom scroll hacker news" -> {"sources": ["hn"], "vibe": "doom", "limit": 30}
            - "snarky summary of AI news" -> {"sources": ["gnews:AI artificial intelligence", "hn", "verge", "arxiv:artificial intelligence", "techcrunch"], "vibe": "snarky", "topics": ["ai"]}
            - "latest AI research" -> {"sources": ["arxiv:large language models", "gnews:AI research", "hn", "ars"], "vibe": "neutral", "topics": ["ai"]}
            - "new pharmaceutical news" -> {"sources": ["gnews:pharmaceutical drug", "bbc:health", "guardian", "arxiv:drug discovery", "sciencedaily"], "vibe": "neutral", "topics": ["pharma"]}
            - "latest health news" -> {"sources": ["gnews_topic:HEALTH", "bbc:health", "npr", "sciencedaily"], "vibe": "neutral", "topics": ["health"]}
            - "climate change policy updates" -> {"sources": ["gnews:climate change policy", "guardian", "bbc:environment", "carbonbrief", "npr"], "vibe": "neutral", "topics": ["climate"]}
            - "business and finance updates" -> {"sources": ["gnews_topic:BUSINESS", "bbc:business", "reuters", "cnn"], "vibe": "neutral", "topics": ["business"]}
            - "what are c# devs talking about" -> {"sources": ["reddit:csharp", "so:csharp", "hn", "devto"], "vibe": "neutral"}
            - "latest space news" -> {"sources": ["spaceflight", "gnews:space launch", "bbc:science", "ars", "arxiv:astrophysics"], "vibe": "neutral", "topics": ["space"]}
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

                    // Enrich LLM result with YAML routing — the LLM often returns
                    // sparse sources (e.g., only gnews). YAML routing ensures full
                    // source spread for the detected topic.
                    EnrichWithYamlRouting(result, prompt);

                    return result;
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
    /// Enrich an interpreted prompt with YAML-driven routing.
    /// The LLM often returns sparse sources (e.g., only gnews for an AI query).
    /// YAML routing ensures the full source spread for the detected topic
    /// (e.g., AI → hn, bbc:technology, verge, techcrunch, reddit).
    /// </summary>
    private void EnrichWithYamlRouting(InterpretedPrompt result, string prompt)
    {
        var router = GetRouter();
        var detectedTopic = router.DetectTopic(prompt);
        if (detectedTopic == "default") return;

        var routing = router.RouteByTopic(detectedTopic, prompt);

        // Add YAML-routed sources that the LLM didn't include
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

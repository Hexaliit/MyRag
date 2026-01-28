using System.Text.Json.Serialization;

namespace DoomSummarizer.Services;

/// <summary>
/// Structured intent output from the sentinel LLM.
/// The sentinel classifies the query into structured parameters (categories, tone, intent)
/// instead of selecting specific sources — source selection is done heuristically
/// by <see cref="SentinelSourceMapper"/>.
/// </summary>
public record SentinelIntent
{
    /// <summary>
    /// Query intent type: news, research, howto, roundup, opinion, qa, deep_dive.
    /// </summary>
    [JsonPropertyName("intent")]
    public string Intent { get; init; } = "news";

    /// <summary>
    /// Category weights (0.0–1.0). Keys match sources.yaml routing categories:
    /// technology, programming, health, pharma, science, environment, climate,
    /// business, finance, politics, world, entertainment, humor, sports,
    /// ai, security, space, disaster, factcheck.
    /// </summary>
    [JsonPropertyName("categories")]
    public Dictionary<string, double> Categories { get; init; } = new();

    /// <summary>
    /// Output tone: neutral, doom, hopeful, snarky, funny, upbeat, friendly.
    /// </summary>
    [JsonPropertyName("tone")]
    public string Tone { get; init; } = "neutral";

    /// <summary>
    /// How time-sensitive is this query: breaking, today, week, any.
    /// </summary>
    [JsonPropertyName("time_sensitivity")]
    public string TimeSensitivity { get; init; } = "any";

    /// <summary>
    /// Specific search terms to use for search-based sources (gnews, duckduckgo).
    /// </summary>
    [JsonPropertyName("search_queries")]
    public List<string> SearchQueries { get; init; } = [];

    /// <summary>
    /// Named entities extracted by the sentinel (people, orgs, places).
    /// </summary>
    [JsonPropertyName("entities")]
    public List<string> Entities { get; init; } = [];

    /// <summary>
    /// Sources the user explicitly named (e.g., "hacker news" → "hn", "bbc" → "bbc").
    /// Only populated when the user directly references a source by name.
    /// </summary>
    [JsonPropertyName("explicit_sources")]
    public List<string> ExplicitSources { get; init; } = [];

    /// <summary>
    /// Requested item limit.
    /// </summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 20;

    /// <summary>
    /// GraphRAG query scope: "local" (specific), "global" (sensemaking), "connective" (DRIFT).
    /// When "global" or "connective", entity graph enrichment is auto-enabled.
    /// See: https://microsoft.github.io/graphrag/
    /// </summary>
    [JsonPropertyName("graph_scope")]
    public string? GraphScope { get; init; }
}

/// <summary>
/// Heuristically maps a <see cref="SentinelIntent"/> to concrete CLI source identifiers
/// using the YAML-driven <see cref="SourceRouter"/> routing rules.
/// The sentinel LLM only classifies the query — this class picks the actual sources.
/// </summary>
public static class SentinelSourceMapper
{
    /// <summary>
    /// Maximum total sources to select (avoids excessive fetching).
    /// </summary>
    private const int MaxTotalSources = 10;

    /// <summary>
    /// Minimum category weight to include sources from that category.
    /// </summary>
    private const double MinCategoryWeight = 0.15;

    /// <summary>
    /// Sources that only make sense for technology/programming queries.
    /// </summary>
    private static readonly HashSet<string> TechOnlySources = new(StringComparer.OrdinalIgnoreCase)
    {
        "hn", "lobsters", "devto", "techcrunch", "wired", "theregister", "ars", "verge"
    };

    /// <summary>
    /// Sources that are archives/research, not current news.
    /// Excluded from roundup and breaking news intents.
    /// </summary>
    private static readonly HashSet<string> ArchiveSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "arxiv", "wikipedia"
    };

    /// <summary>
    /// Categories that imply tech content.
    /// </summary>
    private static readonly HashSet<string> TechCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "technology", "programming", "ai", "security"
    };

    /// <summary>
    /// Valid YAML routing categories. Used to validate sentinel output.
    /// </summary>
    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "technology", "ai", "security", "programming", "science", "health", "pharma",
        "business", "finance", "politics", "world", "entertainment", "humor", "sports",
        "environment", "climate", "space", "disaster", "factcheck", "default"
    };

    /// <summary>
    /// Map unknown sentinel categories to valid routing categories.
    /// Local LLMs sometimes invent categories not in our YAML routing.
    /// </summary>
    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geography"] = "world",
        ["news"] = "default",
        ["general"] = "default",
        ["culture"] = "entertainment",
        ["education"] = "science",
        ["travel"] = "world",
        ["food"] = "default",
        ["law"] = "politics",
        ["legal"] = "politics",
        ["military"] = "world",
        ["energy"] = "environment",
        ["crypto"] = "finance",
        ["gaming"] = "technology",
        ["llm"] = "ai",
        ["llms"] = "ai",
        ["machine_learning"] = "ai",
        ["robotics"] = "technology",
        ["music"] = "entertainment",
        ["film"] = "entertainment",
        ["tv"] = "entertainment",
        ["medicine"] = "health",
        ["weather"] = "environment",
        ["astronomy"] = "space",
        ["economics"] = "business",
    };

    /// <summary>
    /// Map a sentinel intent to concrete source identifiers for the fetch pipeline.
    /// </summary>
    /// <param name="intent">Structured intent from sentinel LLM.</param>
    /// <param name="router">YAML source router for category → source lookups.</param>
    /// <param name="query">Original user query (for search source parameterization).</param>
    /// <param name="nerContext">Optional NER context for entity-specific source enrichment.</param>
    /// <returns>Ordered list of CLI source identifiers (e.g., "gnews:AI news", "bbc:technology", "hn").</returns>
    public static List<string> MapToSources(
        SentinelIntent intent,
        SourceRouter router,
        string query,
        QueryNerContext? nerContext = null)
    {
        var sources = new List<string>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Null-guard all collection properties — JSON deserialization can set them to null
        // even though the record has default initializers.
        var categories = intent.Categories ?? new();
        var explicitSources = intent.ExplicitSources ?? [];
        var intentSearchQueries = intent.SearchQueries ?? [];

        var isRoundup = intent.Intent is "roundup" or "news" &&
                        intent.TimeSensitivity is "today" or "breaking";
        var isResearch = intent.Intent is "research" or "deep_dive";
        var isQA = intent.Intent is "qa" or "howto";
        var hasTechCategory = categories.Any(c =>
            TechCategories.Contains(c.Key) && c.Value >= MinCategoryWeight);

        // --- Phase 1: Explicit user-named sources (always honored) ---
        foreach (var src in explicitSources)
        {
            AddSource(sources, usedRoots, src);
        }

        // --- Phase 2: Search queries as gnews/search sources ---
        // For QA/howto, search is the primary answer source — also add the raw query
        // since it carries more context than extracted keywords.
        // Sentinel queries come first (spelling-corrected, expanded abbreviations),
        // raw query appended as backup with full context.
        var searchQueries = intentSearchQueries.ToList();
        if (isQA && !string.IsNullOrWhiteSpace(query))
        {
            var rawTerms = query.Trim();
            if (!searchQueries.Any(sq => sq.Contains(rawTerms, StringComparison.OrdinalIgnoreCase) ||
                                         rawTerms.Contains(sq, StringComparison.OrdinalIgnoreCase)))
            {
                searchQueries.Add(rawTerms); // backup after sentinel's crafted queries
            }
        }

        // QA gets more search slots (answer is in search results, not RSS feeds)
        var maxSearchQueries = isQA ? 4 : 3;
        foreach (var sq in searchQueries.Take(maxSearchQueries))
        {
            if (!usedRoots.Contains("gnews"))
                AddSource(sources, usedRoots, $"gnews:{sq}");
            else if (!sources.Any(s => s.StartsWith("gnews:", StringComparison.OrdinalIgnoreCase) &&
                                      s.Contains(sq, StringComparison.OrdinalIgnoreCase)))
                sources.Add($"gnews:{sq}"); // allow multiple gnews with different queries

            if (!usedRoots.Contains("search"))
                AddSource(sources, usedRoots, $"search:{sq}");
        }

        // If no search queries but we have a query, add a default gnews search
        if (searchQueries.Count == 0 && !string.IsNullOrWhiteSpace(query) && !usedRoots.Contains("gnews"))
        {
            var topicTerms = ExtractTopicTerms(query);
            if (!string.IsNullOrEmpty(topicTerms))
                AddSource(sources, usedRoots, $"gnews:{topicTerms}");
        }

        // --- Phase 3: Category-weighted source selection ---
        // For QA, search results are the primary answer source — feed sources provide
        // context/breadth only. Cap total feed sources to avoid drowning search results.
        // Normalize unknown categories to valid YAML routing categories.
        var sortedCategories = categories
            .Select(kv =>
            {
                var normalizedKey = ValidCategories.Contains(kv.Key) ? kv.Key
                    : CategoryAliases.GetValueOrDefault(kv.Key, "default");
                return new KeyValuePair<string, double>(normalizedKey, kv.Value);
            })
            .Where(kv => kv.Value >= MinCategoryWeight && kv.Key != "default")
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, double>(g.Key, g.Max(kv => kv.Value)))
            .OrderByDescending(kv => kv.Value)
            .ToList();

        // If no categories detected, use default routing
        if (sortedCategories.Count == 0)
            sortedCategories = [new KeyValuePair<string, double>("default", 0.5)];

        var totalFeedSourcesAdded = 0;
        var maxTotalFeedSources = isQA ? 3 : MaxTotalSources; // QA: max 3 feed sources total

        foreach (var (category, weight) in sortedCategories)
        {
            if (sources.Count >= MaxTotalSources) break;
            if (totalFeedSourcesAdded >= maxTotalFeedSources) break;

            var routing = router.RouteByTopic(category, query);

            // Higher weight → more sources from this category
            // QA needs fewer RSS/feed sources (search results are the primary answer source)
            var maxFromCategory = isQA
                ? Math.Max(1, (int)Math.Ceiling(weight * 2))   // QA: 1-2 feed sources
                : Math.Max(1, (int)Math.Ceiling(weight * 4));  // News/roundup: 1-4 feed sources
            var added = 0;

            foreach (var yamlSource in routing.Sources)
            {
                if (added >= maxFromCategory) break;
                if (sources.Count >= MaxTotalSources) break;

                // Skip archive sources for roundups
                if (isRoundup && ArchiveSources.Contains(yamlSource))
                    continue;

                // Skip tech-only sources when query isn't about tech
                if (!hasTechCategory && TechOnlySources.Contains(yamlSource))
                    continue;

                // Skip search engines (already handled in Phase 2)
                if (yamlSource is "google_news" or "duckduckgo")
                    continue;

                var mapped = MapYamlSourceToCli(yamlSource, routing, query);
                if (mapped == null) continue;

                var root = mapped.Split(':')[0];
                if (usedRoots.Contains(root)) continue;

                AddSource(sources, usedRoots, mapped);
                added++;
                totalFeedSourcesAdded++;
            }
        }

        // --- Phase 4: Research enrichment ---
        if (isResearch && !usedRoots.Contains("arxiv") && sources.Count < MaxTotalSources)
        {
            var topicTerms = ExtractTopicTerms(query);
            var arxivQuery = !string.IsNullOrEmpty(topicTerms) ? $"arxiv:{topicTerms}" : "arxiv";
            AddSource(sources, usedRoots, arxivQuery);
        }

        // --- Phase 5: NER entity enrichment (search queries only) ---
        // NER adds entity-specific search queries but does NOT add news outlet preferences.
        // Entity type (ORG/PER/LOC) doesn't determine topic category — that's the sentinel's job.
        // e.g., "SNL" is ORG but entertainment, not business.
        if (nerContext?.HasEntities == true)
        {
            foreach (var eq in nerContext.EntityQueries.Take(2))
            {
                var gnewsQuery = $"gnews:{eq.EntityText}";
                if (!sources.Any(s => s.Contains(eq.EntityText, StringComparison.OrdinalIgnoreCase)))
                {
                    if (sources.Count < MaxTotalSources)
                        sources.Add(gnewsQuery);
                }
            }
        }

        // --- Phase 6: Minimum diversity floor ---
        if (sources.Count < 3)
        {
            var defaultRouting = router.RouteByTopic("default", query);
            foreach (var src in defaultRouting.Sources)
            {
                var mapped = MapYamlSourceToCli(src, defaultRouting, query);
                if (mapped == null) continue;

                var root = mapped.Split(':')[0];
                if (usedRoots.Contains(root)) continue;

                AddSource(sources, usedRoots, mapped);
                if (sources.Count >= 4) break;
            }
        }

        return sources.Take(MaxTotalSources).ToList();
    }

    /// <summary>
    /// Convert a SentinelIntent into an InterpretedPrompt for backward compatibility
    /// with the existing pipeline.
    /// </summary>
    public static InterpretedPrompt ToInterpretedPrompt(
        SentinelIntent intent,
        SourceRouter router,
        string query,
        QueryNerContext? nerContext = null)
    {
        var sources = MapToSources(intent, router, query, nerContext);

        // Extract topics from top-weighted categories
        var categories = intent.Categories ?? new();
        var topics = categories
            .Where(kv => kv.Value >= 0.3)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        return new InterpretedPrompt
        {
            RawPrompt = query,
            Sources = sources,
            Vibe = intent.Tone ?? "neutral",
            SearchQueries = (intent.SearchQueries ?? []).ToList(),
            Websites = [],
            Limit = intent.Limit > 0 ? intent.Limit : 20,
            Topics = topics,
            NerContext = nerContext,
            SentinelIntent = intent,
            GraphScope = QueryTypeDetector.DetectGraphScope(query, intent)
        };
    }

    #region Helpers

    private static void AddSource(List<string> sources, HashSet<string> usedRoots, string source)
    {
        if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(source);
            usedRoots.Add(source.Split(':')[0]);
        }
    }

    /// <summary>
    /// Map a YAML source name to CLI source identifier.
    /// </summary>
    internal static string? MapYamlSourceToCli(string yamlSource, RoutingResult routing, string query)
    {
        return yamlSource switch
        {
            "google_news" => !string.IsNullOrEmpty(query)
                ? $"gnews:{ExtractTopicTerms(query)}"
                : routing.GoogleNewsTopic != null
                    ? $"gnews_topic:{routing.GoogleNewsTopic}"
                    : "gnews",
            "duckduckgo" => !string.IsNullOrEmpty(query)
                ? $"search:{ExtractTopicTerms(query)}"
                : null,
            "bbc" => routing.BbcCategory != null
                ? $"bbc:{routing.BbcCategory}"
                : "bbc",
            "arxiv" => !string.IsNullOrEmpty(query)
                ? $"arxiv:{ExtractTopicTerms(query)}"
                : "arxiv",
            // Direct passthrough for sources that don't need transformation
            "guardian" or "cnn" or "reuters" or "hn" or "reddit" or "ars" or "verge"
                or "lobsters" or "devto" or "techcrunch" or "wired" or "theregister"
                or "npr" or "sciencedaily" or "phys" or "carbonbrief" or "spaceflight"
                or "earthquake" or "factcheck" or "wikipedia" or "theonion" or "babylonbee"
                => yamlSource,
            _ => yamlSource // Unknown sources passed through
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

    internal static string ExtractTopicTerms(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w) && w.Length > 1)
            .ToList();
        return words.Count > 0 ? string.Join(" ", words) : "";
    }

    #endregion
}

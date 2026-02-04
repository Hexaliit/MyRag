using System.Reflection;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
///     YAML-driven source routing. Detects topics from query keywords
///     and returns the appropriate sources, BBC category, and Google News topic.
///     Supports semantic embedding-based fuzzy topic matching when EmbeddingService is available.
/// </summary>
public class SourceRouter
{
    private const float SemanticThreshold = 0.35f;
    private readonly SourceRoutingConfig _config;
    private IEmbeddingService? _embedding;
    private Dictionary<string, float[]>? _topicEmbeddings;

    public SourceRouter(SourceRoutingConfig config)
    {
        _config = config;
    }

    /// <summary>
    ///     All configured source names.
    /// </summary>
    public IEnumerable<string> AllSources => _config.Sources.Keys;

    /// <summary>
    ///     All routing categories.
    /// </summary>
    public IEnumerable<string> AllTopics => _config.Routing.Keys;

    /// <summary>
    ///     Whether semantic embeddings are initialized.
    /// </summary>
    public bool HasEmbeddings => _topicEmbeddings != null;

    /// <summary>
    ///     Load sources config from embedded YAML resource.
    /// </summary>
    public static SourceRouter Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("sources.yaml", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException("Embedded sources.yaml not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var config = deserializer.Deserialize<SourceRoutingConfig>(yaml)
                     ?? throw new InvalidOperationException("Failed to deserialize sources.yaml (empty or invalid)");
        return new SourceRouter(config);
    }

    /// <summary>
    ///     Initialize semantic embeddings for all topic keywords.
    ///     Call this once after embedding service is available to enable fuzzy topic matching.
    /// </summary>
    public async Task InitializeEmbeddingsAsync(IEmbeddingService embedding)
    {
        _embedding = embedding;
        _topicEmbeddings = new Dictionary<string, float[]>();

        foreach (var (topic, keywords) in _config.TopicKeywords)
        {
            // Create representative text from all keywords for this topic
            var representativeText = string.Join(" ", keywords);
            _topicEmbeddings[topic] = await embedding.EmbedAsync(representativeText);
        }
    }

    /// <summary>
    ///     Detect the best routing category for a query.
    ///     Uses semantic embedding similarity when available, falls back to keyword matching.
    ///     Returns the category name (e.g., "health", "technology") or "default".
    /// </summary>
    public async Task<string> DetectTopicAsync(string query)
    {
        // Try semantic matching first (fuzzy, handles synonyms)
        if (_topicEmbeddings != null && _embedding != null)
        {
            var semanticTopic = await DetectTopicSemanticAsync(query);
            if (semanticTopic != "default")
                return semanticTopic;
        }

        // Fall back to keyword matching (exact, fast)
        return DetectTopicKeyword(query);
    }

    /// <summary>
    ///     Detect topic using semantic embedding similarity.
    ///     Embeds the query and finds the topic with highest cosine similarity.
    /// </summary>
    internal async Task<string> DetectTopicSemanticAsync(string query)
    {
        if (_topicEmbeddings == null || _embedding == null || _topicEmbeddings.Count == 0)
            return "default";

        var queryEmbedding = await _embedding.EmbedAsync(query);

        var bestTopic = "default";
        var bestScore = SemanticThreshold;

        foreach (var (topic, topicEmbedding) in _topicEmbeddings)
        {
            var similarity = VectorMath.CosineSimilarity(queryEmbedding, topicEmbedding);
            if (similarity > bestScore)
            {
                bestScore = similarity;
                bestTopic = topic;
            }
        }

        return bestTopic;
    }

    /// <summary>
    ///     Detect topic using exact keyword matching.
    /// </summary>
    internal string DetectTopicKeyword(string query)
    {
        var lower = query.ToLowerInvariant();
        var wordSet = new HashSet<string>(
            lower.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        var bestTopic = "default";
        var bestScore = 0;

        foreach (var (topic, keywords) in _config.TopicKeywords)
        {
            var score = 0;
            foreach (var kw in keywords)
            {
                if (kw.Contains(' '))
                {
                    // Multi-word keywords: check substring (already lowered in YAML)
                    if (lower.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        score++;
                }
                else
                {
                    // Single-word: O(1) HashSet lookup instead of O(N) array scan
                    if (wordSet.Contains(kw))
                        score++;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestTopic = topic;
            }
        }

        return bestTopic;
    }

    /// <summary>
    ///     Get the routing result for a query: which sources to use, BBC category, Google News topic.
    /// </summary>
    public async Task<RoutingResult> RouteAsync(string query)
    {
        var topic = await DetectTopicAsync(query);
        return RouteByTopic(topic, query);
    }

    /// <summary>
    ///     Route by a specific topic name.
    /// </summary>
    public RoutingResult RouteByTopic(string topic, string? query = null)
    {
        var rule = _config.Routing.GetValueOrDefault(topic)
                   ?? _config.Routing.GetValueOrDefault("default")
                   ?? new RoutingRule { Sources = ["google_news", "bbc"] };

        return new RoutingResult
        {
            Topic = topic,
            Sources = rule.Sources,
            BbcCategory = rule.BbcCategory,
            GoogleNewsTopic = rule.GoogleNewsTopic,
            Query = query
        };
    }

    /// <summary>
    ///     Get the feed URLs for a source + category combination.
    ///     E.g., GetFeeds("bbc", "health") returns the BBC health RSS feed URL.
    /// </summary>
    public List<string> GetFeeds(string sourceName, string? category = null)
    {
        if (!_config.Sources.TryGetValue(sourceName, out var source))
            return [];

        if (source.Feeds == null)
            return [];

        // Try category-specific feed first, then fall back to default
        if (!string.IsNullOrEmpty(category) && source.Feeds.TryGetValue(category, out var categoryFeeds))
            return categoryFeeds;

        return source.Feeds.GetValueOrDefault("default") ?? [];
    }

    /// <summary>
    ///     Get the source definition by name.
    /// </summary>
    public SourceDefinition? GetSource(string name)
    {
        return _config.Sources.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Score a source for a given intent, category weights, and time sensitivity.
    ///     Formula: (intentAffinity × 0.6) + (categoryMatch × 0.3) + (capabilityBonus × 0.1)
    ///     Returns 0.0 for unknown sources.
    /// </summary>
    public double ScoreSource(string sourceName, string intent,
        Dictionary<string, double> categories, string timeSensitivity = "any")
    {
        var source = GetSource(sourceName);
        if (source == null) return 0.0;

        var intentScore = GetIntentAffinity(source, intent);
        var categoryScore = GetCategoryMatch(source, sourceName, categories);
        var capabilityScore = GetCapabilityBonus(source, intent, timeSensitivity);

        return Math.Clamp(intentScore * 0.6 + categoryScore * 0.3 + capabilityScore * 0.1, 0.0, 1.0);
    }

    /// <summary>
    ///     Check if a source has a specific capability tag.
    /// </summary>
    public bool HasCapability(string sourceName, string capability)
    {
        var source = GetSource(sourceName);
        return source?.Capabilities?.Contains(capability, StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     Get all source names that have a specific capability.
    /// </summary>
    public IEnumerable<string> GetSourcesWithCapability(string capability)
    {
        return _config.Sources
            .Where(kv => kv.Value.Capabilities?.Contains(capability, StringComparer.OrdinalIgnoreCase) == true)
            .Select(kv => kv.Key);
    }

    /// <summary>
    ///     Get intent affinity from YAML, with type-derived fallback.
    /// </summary>
    private static double GetIntentAffinity(SourceDefinition source, string intent)
    {
        // Try YAML intent_affinity first
        if (source.IntentAffinity != null &&
            source.IntentAffinity.TryGetValue(intent, out var affinity))
            return affinity;

        // Type-derived fallback when no YAML affinity is set
        return source.Type switch
        {
            "rss" or "hackernews" or "reddit" => intent switch
            {
                "news" or "roundup" => 0.7,
                "trend" => 0.5,
                "qa" or "howto" => 0.2,
                "research" or "deep_dive" => 0.3,
                _ => 0.4
            },
            "google_news_rss" => intent switch
            {
                "news" or "roundup" => 0.8,
                "qa" or "howto" => 0.3,
                _ => 0.5
            },
            "duckduckgo" or "google_search" => intent switch
            {
                "qa" or "howto" or "search_only" => 0.8,
                "news" => 0.4,
                _ => 0.5
            },
            "api" => intent switch
            {
                "qa" or "research" => 0.5,
                "news" => 0.4,
                _ => 0.3
            },
            _ => 0.3
        };
    }

    /// <summary>
    ///     Category match score: max of (routing rule membership, scope overlap).
    /// </summary>
    private double GetCategoryMatch(SourceDefinition source, string sourceName,
        Dictionary<string, double> categories)
    {
        if (categories.Count == 0) return 0.3; // neutral default

        var maxScore = 0.0;

        foreach (var (category, weight) in categories)
        {
            // Check if this source is in the routing rule for this category
            var rule = _config.Routing.GetValueOrDefault(category);
            if (rule?.Sources.Contains(sourceName, StringComparer.OrdinalIgnoreCase) == true)
            {
                maxScore = Math.Max(maxScore, weight);
                continue;
            }

            // Check scope overlap
            if (source.Scope?.Any(s => s.Equals(category, StringComparison.OrdinalIgnoreCase)) == true)
                maxScore = Math.Max(maxScore, weight * 0.7); // scope match is weaker than routing rule
        }

        return maxScore;
    }

    /// <summary>
    ///     Capability bonus/penalty based on intent and time sensitivity.
    /// </summary>
    private static double GetCapabilityBonus(SourceDefinition source, string intent, string timeSensitivity)
    {
        var caps = source.Capabilities;
        if (caps == null || caps.Count == 0) return 0.0;

        var bonus = 0.0;

        var hasKnowledge = caps.Contains("knowledge", StringComparer.OrdinalIgnoreCase);
        var hasAcademic = caps.Contains("academic", StringComparer.OrdinalIgnoreCase);
        var hasArchive = caps.Contains("archive", StringComparer.OrdinalIgnoreCase);
        var hasRealtime = caps.Contains("realtime", StringComparer.OrdinalIgnoreCase);

        // Knowledge sources get bonus for QA/howto
        if (hasKnowledge && intent is "qa" or "howto" or "search_only")
            bonus += 0.5;

        // Academic sources get bonus for research
        if (hasAcademic && intent is "research" or "deep_dive")
            bonus += 0.5;

        // Realtime sources get bonus for breaking news
        if (hasRealtime && timeSensitivity is "breaking" or "today")
            bonus += 0.3;

        // Archive sources get penalty for breaking/today queries
        if (hasArchive && timeSensitivity is "breaking" or "today")
            bonus -= 0.3;

        return bonus;
    }
}

/// <summary>
///     Result of routing a query to sources.
/// </summary>
public record RoutingResult
{
    public required string Topic { get; init; }
    public required List<string> Sources { get; init; }
    public string? BbcCategory { get; init; }
    public string? GoogleNewsTopic { get; init; }
    public string? Query { get; init; }
}
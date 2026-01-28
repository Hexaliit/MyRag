using System.Reflection;
using DoomSummarizer.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
/// YAML-driven source routing. Detects topics from query keywords
/// and returns the appropriate sources, BBC category, and Google News topic.
/// Supports semantic embedding-based fuzzy topic matching when EmbeddingService is available.
/// </summary>
public class SourceRouter
{
    private readonly SourceRoutingConfig _config;
    private Dictionary<string, float[]>? _topicEmbeddings;
    private EmbeddingService? _embedding;
    private const float SemanticThreshold = 0.35f;

    public SourceRouter(SourceRoutingConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Load sources config from embedded YAML resource.
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
    /// Initialize semantic embeddings for all topic keywords.
    /// Call this once after embedding service is available to enable fuzzy topic matching.
    /// </summary>
    public void InitializeEmbeddings(EmbeddingService embedding)
    {
        _embedding = embedding;
        _topicEmbeddings = new Dictionary<string, float[]>();

        foreach (var (topic, keywords) in _config.TopicKeywords)
        {
            // Create representative text from all keywords for this topic
            var representativeText = string.Join(" ", keywords);
            _topicEmbeddings[topic] = embedding.Embed(representativeText);
        }
    }

    /// <summary>
    /// Detect the best routing category for a query.
    /// Uses semantic embedding similarity when available, falls back to keyword matching.
    /// Returns the category name (e.g., "health", "technology") or "default".
    /// </summary>
    public string DetectTopic(string query)
    {
        // Try semantic matching first (fuzzy, handles synonyms)
        if (_topicEmbeddings != null && _embedding != null)
        {
            var semanticTopic = DetectTopicSemantic(query);
            if (semanticTopic != "default")
                return semanticTopic;
        }

        // Fall back to keyword matching (exact, fast)
        return DetectTopicKeyword(query);
    }

    /// <summary>
    /// Detect topic using semantic embedding similarity.
    /// Embeds the query and finds the topic with highest cosine similarity.
    /// </summary>
    internal string DetectTopicSemantic(string query)
    {
        if (_topicEmbeddings == null || _embedding == null || _topicEmbeddings.Count == 0)
            return "default";

        var queryEmbedding = _embedding.Embed(query);

        var bestTopic = "default";
        var bestScore = SemanticThreshold;

        foreach (var (topic, topicEmbedding) in _topicEmbeddings)
        {
            var similarity = EmbeddingService.CosineSimilarity(queryEmbedding, topicEmbedding);
            if (similarity > bestScore)
            {
                bestScore = similarity;
                bestTopic = topic;
            }
        }

        return bestTopic;
    }

    /// <summary>
    /// Detect topic using exact keyword matching.
    /// </summary>
    internal string DetectTopicKeyword(string query)
    {
        var lower = query.ToLowerInvariant();
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Score each topic by how many keywords match
        var scores = new Dictionary<string, int>();

        foreach (var (topic, keywords) in _config.TopicKeywords)
        {
            var score = keywords.Count(kw =>
            {
                // Multi-word keywords: check substring
                if (kw.Contains(' '))
                    return lower.Contains(kw.ToLowerInvariant());
                // Single-word: check word boundary
                return words.Contains(kw.ToLowerInvariant());
            });

            if (score > 0)
                scores[topic] = score;
        }

        if (scores.Count == 0)
            return "default";

        // Return the topic with the highest keyword match count
        return scores.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>
    /// Get the routing result for a query: which sources to use, BBC category, Google News topic.
    /// </summary>
    public RoutingResult Route(string query)
    {
        var topic = DetectTopic(query);
        return RouteByTopic(topic, query);
    }

    /// <summary>
    /// Filter sources to only those whose scope matches the detected topic.
    /// Returns sources that explicitly include the topic in their scope,
    /// plus search sources (which work for any topic).
    /// </summary>
    public List<string> FilterSourcesByScope(List<string> sources, string topic)
    {
        if (topic == "default" || topic == "general")
            return sources; // No filtering for general queries

        var filtered = new List<string>();
        foreach (var sourceName in sources)
        {
            var source = GetSource(sourceName);
            if (source == null)
            {
                filtered.Add(sourceName); // Unknown source — include it
                continue;
            }

            // Always include search sources (they can search anything)
            if (source.Search)
            {
                filtered.Add(sourceName);
                continue;
            }

            // Include if scope is null (legacy, assume general) or matches topic
            if (source.Scope == null || source.Scope.Count == 0 ||
                source.Scope.Any(s => s.Equals(topic, StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add(sourceName);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Route by a specific topic name.
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
    /// Get the feed URLs for a source + category combination.
    /// E.g., GetFeeds("bbc", "health") returns the BBC health RSS feed URL.
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
    /// Get the source definition by name.
    /// </summary>
    public SourceDefinition? GetSource(string name) =>
        _config.Sources.GetValueOrDefault(name);

    /// <summary>
    /// All configured source names.
    /// </summary>
    public IEnumerable<string> AllSources => _config.Sources.Keys;

    /// <summary>
    /// All routing categories.
    /// </summary>
    public IEnumerable<string> AllTopics => _config.Routing.Keys;

    /// <summary>
    /// Whether semantic embeddings are initialized.
    /// </summary>
    public bool HasEmbeddings => _topicEmbeddings != null;
}

/// <summary>
/// Result of routing a query to sources.
/// </summary>
public record RoutingResult
{
    public required string Topic { get; init; }
    public required List<string> Sources { get; init; }
    public string? BbcCategory { get; init; }
    public string? GoogleNewsTopic { get; init; }
    public string? Query { get; init; }
}

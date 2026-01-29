using System.Text.Json.Serialization;
using DoomSummarizer.Services;

namespace DoomSummarizer.Models;

public record ContentItem
{
    public required string Id { get; init; }
    public required string Source { get; init; } // "hn", "reddit", "web"
    public required string Title { get; init; }
    public string? Url { get; set; }
    public string? Content { get; set; }
    public string? Author { get; init; }
    /// <summary>
    /// True if Content has been enriched from the actual page (replacing RSS description).
    /// </summary>
    public bool IsEnriched { get; set; }
    public int Score { get; init; }
    public int CommentCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ImageUrl { get; set; } // og:image, thumbnail, etc.

    // Computed after analysis
    public string? Summary { get; set; }
    public float[]? Embedding { get; set; }
    public string? DetectedTopic { get; set; }
    public float SentimentScore { get; set; } // -1 to 1
    public List<string> Tags { get; set; } = [];

    // Document-level keyword profile (set by DocumentProfileService)
    public string? Keywords { get; set; }

    // Relevance scoring (set by RelevanceScorer)
    public double RelevanceScore { get; set; } // Combined RRF score (0-1 range)

    // Structural analysis (populated by Markdown content extraction)
    public ContentStructure? ContentStructure { get; set; }

    // One-hop linked content (populated by LinkFollowingService)
    public List<LinkedPage> LinkedPages { get; set; } = [];

    // Structured metadata from API sources (Places, etc.)
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Content extracted from a linked page (one-hop follow).
/// </summary>
public record LinkedPage
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string? SiteName { get; init; }
}

public record StoredItem
{
    public long RowId { get; init; }
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Summary { get; init; }
    public string? Content { get; init; }
    public float SentimentScore { get; init; }
    public string? DetectedTopic { get; init; }
    public string? Tags { get; init; } // JSON array
    public int Score { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public byte[]? Embedding { get; init; }
    public string? Keywords { get; init; }

    /// <summary>
    /// Convert a stored item back to a ContentItem for ranking/display.
    /// </summary>
    public ContentItem ToContentItem() => new()
    {
        Id = Id,
        Source = Source,
        Title = Title,
        Url = Url,
        Content = Content ?? Summary ?? Title,
        Summary = Summary,
        DetectedTopic = DetectedTopic,
        SentimentScore = SentimentScore,
        Score = Score,
        CreatedAt = CreatedAt,
        FetchedAt = FetchedAt,
        Embedding = Embedding != null ? EmbeddingService.FromBytes(Embedding) : null,
        Keywords = Keywords,
        Tags = DeserializeTags(Tags)
    };

    private static List<string> DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(tagsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public record TrendAnalysis
{
    public required DateTimeOffset StartDate { get; init; }
    public required DateTimeOffset EndDate { get; init; }
    public int TotalItems { get; init; }
    public float AverageSentiment { get; init; }
    public float SentimentChange { get; init; } // vs previous period
    public List<TopicTrend> TopTopics { get; init; } = [];
    public List<string> EmergingKeywords { get; init; } = [];
    public List<string> DecliningKeywords { get; init; } = [];
}

public record TopicTrend
{
    public required string Topic { get; init; }
    public int Count { get; init; }
    public float AverageSentiment { get; init; }
    public float Change { get; init; } // % change vs previous period
}

// Knowledge graph models
public record GraphEntity
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; } // PER, ORG, LOC, MISC
    public int MentionCount { get; init; }
    public int ArticleCount { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>
    /// Freshness-weighted score: recent mentions count more.
    /// </summary>
    public double FreshnessScore
    {
        get
        {
            var ageHours = (DateTimeOffset.UtcNow - LastSeen).TotalHours;
            var decay = Math.Exp(-ageHours / 72.0); // Half-life ~3 days
            return MentionCount * decay;
        }
    }
}

public record GraphRelationship
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required string Type { get; init; }
    public float Weight { get; init; }
    public required string SourceName { get; init; }
    public required string TargetName { get; init; }
}

// API response models
public record HnStory
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("by")] public string? By { get; init; }
    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("descendants")] public int Descendants { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
}

public record RedditListing
{
    [JsonPropertyName("data")] public RedditListingData? Data { get; init; }
}

public record RedditListingData
{
    [JsonPropertyName("children")] public List<RedditChild>? Children { get; init; }
}

public record RedditChild
{
    [JsonPropertyName("data")] public RedditPost? Data { get; init; }
}

public record RedditPost
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("selftext")] public string? Selftext { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("created_utc")] public double CreatedUtc { get; init; }
    [JsonPropertyName("num_comments")] public int NumComments { get; init; }
    [JsonPropertyName("subreddit")] public string? Subreddit { get; init; }
    [JsonPropertyName("is_self")] public bool IsSelf { get; init; }
    [JsonPropertyName("permalink")] public string? Permalink { get; init; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; init; }
    [JsonPropertyName("preview")] public RedditPreview? Preview { get; init; }
}

public record RedditPreview
{
    [JsonPropertyName("images")] public List<RedditImage>? Images { get; init; }
}

public record RedditImage
{
    [JsonPropertyName("source")] public RedditImageSource? Source { get; init; }
}

public record RedditImageSource
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
}

// JSON context for API responses
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HnStory))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(RedditListing))]
[JsonSerializable(typeof(RedditListingData))]
[JsonSerializable(typeof(RedditChild))]
[JsonSerializable(typeof(RedditPost))]
[JsonSerializable(typeof(RedditPreview))]
[JsonSerializable(typeof(RedditImage))]
[JsonSerializable(typeof(RedditImageSource))]
[JsonSerializable(typeof(List<RedditChild>))]
public partial class ApiJsonContext : JsonSerializerContext;

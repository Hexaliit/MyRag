using System.Text.Json.Serialization;

namespace DoomSummarizer.Models;

public record ContentItem
{
    public required string Id { get; init; }
    public required string Source { get; init; } // "hn", "reddit", "web"
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Content { get; init; }
    public string? Author { get; init; }
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
}

public record StoredItem
{
    public long RowId { get; init; }
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Summary { get; init; }
    public float SentimentScore { get; init; }
    public string? DetectedTopic { get; init; }
    public string? Tags { get; init; } // JSON array
    public int Score { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public byte[]? Embedding { get; init; }
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

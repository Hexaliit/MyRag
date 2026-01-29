using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches and analyzes Reddit comments for a post or topic.
/// Useful for understanding community sentiment and discussions.
/// </summary>
public class RedditCommentsFetcher(HttpClient httpClient)
{
    /// <summary>
    /// Fetch top comments for a Reddit post URL.
    /// </summary>
    public async Task<RedditDiscussion> FetchPostCommentsAsync(string postUrl, int commentLimit = 50)
    {
        var discussion = new RedditDiscussion { PostUrl = postUrl };

        try
        {
            // Convert to JSON API URL
            var jsonUrl = postUrl.TrimEnd('/') + ".json?limit=" + commentLimit + "&raw_json=1";

            var request = new HttpRequestMessage(HttpMethod.Get, jsonUrl);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0 (github.com/scottgal/lucidrag)");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var listings = JsonSerializer.Deserialize<RedditListing[]>(json, RedditCommentsJsonContext.Default.RedditListingArray);

            if (listings == null || listings.Length < 2) return discussion;

            // First listing is the post itself
            var postData = listings[0].Data?.Children?.FirstOrDefault()?.Data;
            if (postData != null)
            {
                discussion.PostTitle = postData.Title ?? "";
                discussion.PostContent = postData.Selftext ?? "";
                discussion.PostScore = postData.Score;
                discussion.PostAuthor = postData.Author ?? "";
                discussion.Subreddit = postData.Subreddit ?? "";
            }

            // Second listing is comments - need to deserialize with extended types
            var commentListing = listings[1];
            if (commentListing.Data?.Children != null)
            {
                // Re-serialize and deserialize as extended types
                var commentsJson = JsonSerializer.Serialize(commentListing.Data.Children);
                var extendedChildren = JsonSerializer.Deserialize(commentsJson, RedditCommentsJsonContext.Default.ListRedditChildExtended);
                if (extendedChildren != null)
                {
                    discussion.Comments = ParseComments(extendedChildren, 0);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to fetch Reddit comments: {ex.Message}");
        }

        return discussion;
    }

    /// <summary>
    /// Search for relevant posts in a subreddit and get their top comments.
    /// </summary>
    public async Task<List<RedditDiscussion>> SearchTopicAsync(string subreddit, string query, int postLimit = 5, int commentsPerPost = 10)
    {
        var discussions = new List<RedditDiscussion>();

        try
        {
            var searchUrl = $"https://www.reddit.com/r/{subreddit}/search.json?q={Uri.EscapeDataString(query)}&restrict_sr=on&sort=relevance&limit={postLimit}&raw_json=1";

            var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0 (github.com/scottgal/lucidrag)");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var listing = JsonSerializer.Deserialize(json, ApiJsonContext.Default.RedditListing);

            if (listing?.Data?.Children == null) return discussions;

            foreach (var child in listing.Data.Children.Take(postLimit))
            {
                var post = child.Data;
                if (post?.Permalink == null) continue;

                var postUrl = $"https://www.reddit.com{post.Permalink}";
                var discussion = await FetchPostCommentsAsync(postUrl, commentsPerPost);
                if (discussion.Comments.Count > 0)
                {
                    discussions.Add(discussion);
                }

                // Rate limit
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to search r/{subreddit}: {ex.Message}");
        }

        return discussions;
    }

    private List<RedditComment> ParseComments(List<RedditChildExtended> children, int depth, int maxDepth = 3)
    {
        var comments = new List<RedditComment>();

        foreach (var child in children)
        {
            if (child.Kind != "t1") continue; // Skip non-comment entries
            var data = child.Data;
            if (data == null || string.IsNullOrEmpty(data.Body)) continue;

            var comment = new RedditComment
            {
                Id = data.Id ?? "",
                Author = data.Author ?? "[deleted]",
                Body = data.Body ?? "",
                Score = data.Score,
                Depth = depth,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds((long)(data.CreatedUtc))
            };

            // Parse replies if not too deep
            if (depth < maxDepth && data.Replies.HasValue && data.Replies.Value.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var repliesListing = data.Replies.Value.Deserialize<RedditCommentListing>(RedditCommentsJsonContext.Default.RedditCommentListing);
                    if (repliesListing?.Data?.Children != null)
                    {
                        comment.Replies = ParseComments(repliesListing.Data.Children, depth + 1, maxDepth);
                    }
                }
                catch { /* Replies can be empty string or malformed */ }
            }

            comments.Add(comment);
        }

        return comments.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>
    /// Convert comments to ContentItems for unified processing.
    /// </summary>
    public static List<ContentItem> ToContentItems(RedditDiscussion discussion)
    {
        var items = new List<ContentItem>();

        // Add the post itself
        items.Add(new ContentItem
        {
            Id = $"reddit_post_{discussion.PostUrl.GetHashCode()}",
            Source = "reddit",
            Title = discussion.PostTitle,
            Url = discussion.PostUrl,
            Content = discussion.PostContent,
            Author = discussion.PostAuthor,
            Score = discussion.PostScore,
            CommentCount = discussion.Comments.Count,
            Tags = [discussion.Subreddit]
        });

        // Add top-level comments as items
        foreach (var comment in discussion.Comments.Take(10))
        {
            items.Add(new ContentItem
            {
                Id = $"reddit_comment_{comment.Id}",
                Source = "reddit_comment",
                Title = $"Comment by {comment.Author} (+{comment.Score})",
                Url = discussion.PostUrl,
                Content = comment.Body,
                Author = comment.Author,
                Score = comment.Score,
                CreatedAt = comment.CreatedAt,
                Tags = [discussion.Subreddit, "comment"]
            });
        }

        return items;
    }
}

public class RedditDiscussion
{
    public string PostUrl { get; set; } = "";
    public string PostTitle { get; set; } = "";
    public string PostContent { get; set; } = "";
    public string PostAuthor { get; set; } = "";
    public int PostScore { get; set; }
    public string Subreddit { get; set; } = "";
    public List<RedditComment> Comments { get; set; } = [];
}

public class RedditComment
{
    public string Id { get; set; } = "";
    public string Author { get; set; } = "";
    public string Body { get; set; } = "";
    public int Score { get; set; }
    public int Depth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<RedditComment> Replies { get; set; } = [];
}

// Extended Reddit models for comment parsing
public record RedditCommentData
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("created_utc")] public double CreatedUtc { get; init; }
    [JsonPropertyName("replies")] public JsonElement? Replies { get; init; } // Can be string "" or object

    // Post fields (when this is a post, not a comment)
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("selftext")] public string? Selftext { get; init; }
    [JsonPropertyName("subreddit")] public string? Subreddit { get; init; }
    [JsonPropertyName("permalink")] public string? Permalink { get; init; }
}

public record RedditChildExtended
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("data")] public RedditCommentData? Data { get; init; }
}

public record RedditCommentListing
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("data")] public RedditCommentListingData? Data { get; init; }
}

public record RedditCommentListingData
{
    [JsonPropertyName("children")] public List<RedditChildExtended>? Children { get; init; }
}

[JsonSerializable(typeof(RedditListing[]))]
[JsonSerializable(typeof(RedditChildExtended))]
[JsonSerializable(typeof(List<RedditChildExtended>))]
[JsonSerializable(typeof(RedditCommentData))]
[JsonSerializable(typeof(RedditCommentListing))]
[JsonSerializable(typeof(RedditCommentListingData))]
public partial class RedditCommentsJsonContext : JsonSerializerContext;

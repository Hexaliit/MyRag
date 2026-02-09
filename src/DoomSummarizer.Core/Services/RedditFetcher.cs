using System.Diagnostics;
using System.Text.Json;
using System.Web;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

public class RedditFetcher(HttpClient httpClient)
{
    public async Task<List<ContentItem>> FetchAsync(RedditConfig config, int limit, Action<string>? progress = null)
    {
        var items = new List<ContentItem>();
        var seen = new HashSet<string>();

        foreach (var subreddit in config.Subreddits)
        {
            progress?.Invoke($"Fetching r/{subreddit}...");

            try
            {
                var url = $"https://www.reddit.com/r/{subreddit}/{config.Sort}.json?limit={config.MaxPosts}&raw_json=1";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                // Reddit requires a user agent
                request.Headers.Add("User-Agent", "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)");

                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var listing = JsonSerializer.Deserialize(json, ApiJsonContext.Default.RedditListing);

                if (listing?.Data?.Children == null) continue;

                foreach (var child in listing.Data.Children)
                {
                    var post = child.Data;
                    if (post == null || string.IsNullOrEmpty(post.Id)) continue;
                    if (post.Score < config.MinScore) continue;

                    var id = $"reddit_{post.Id}";
                    if (!seen.Add(id)) continue;

                    // Get best available image
                    string? imageUrl = null;
                    if (post.Preview?.Images?.FirstOrDefault()?.Source?.Url is { } previewUrl)
                        // Reddit HTML-encodes the URL
                        imageUrl = HttpUtility.HtmlDecode(previewUrl);
                    else if (post.Thumbnail is { } thumb &&
                             thumb.StartsWith("http") &&
                             !thumb.Contains("self") &&
                             !thumb.Contains("default"))
                        imageUrl = thumb;

                    var content = post.Selftext;

                    // For link posts (not self posts), fetch content from the target URL
                    if (!post.IsSelf && string.IsNullOrWhiteSpace(content) && !string.IsNullOrEmpty(post.Url))
                    {
                        content = await ContentItemHelpers.FetchLinkContentAsync(httpClient, post.Url);
                    }

                    items.Add(new ContentItem
                    {
                        Id = id,
                        Source = "reddit",
                        Title = post.Title ?? "Untitled",
                        Url = post.IsSelf
                            ? $"https://reddit.com{post.Permalink}"
                            : post.Url,
                        Content = content,
                        Author = post.Author,
                        Score = post.Score,
                        CommentCount = post.NumComments,
                        CreatedAt = DateTimeOffset.FromUnixTimeSeconds((long)post.CreatedUtc),
                        Tags = [post.Subreddit ?? subreddit],
                        ImageUrl = imageUrl
                    });

                    if (items.Count >= limit) break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Warning: Failed to fetch r/{subreddit}: {ex.Message}");
            }

            if (items.Count >= limit) break;

            // Be nice to Reddit
            await Task.Delay(500);
        }

        return items.Take(limit).ToList();
    }
}

using System.Text.Json;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

public class HackerNewsFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://hacker-news.firebaseio.com/v0";

    // Limit concurrent requests to HN Firebase API to avoid hammering it
    private static readonly SemaphoreSlim Throttle = new(5, 5);

    public async Task<List<ContentItem>> FetchAsync(HackerNewsConfig config, int limit, Action<string>? progress = null)
    {
        var items = new List<ContentItem>();
        var seen = new HashSet<long>();

        foreach (var section in config.Sections.Take(3))
        {
            progress?.Invoke($"Fetching HN {section} stories...");

            try
            {
                var endpoint = section.ToLowerInvariant() switch
                {
                    "top" => "topstories",
                    "best" => "beststories",
                    "new" => "newstories",
                    "ask" => "askstories",
                    "show" => "showstories",
                    "job" => "jobstories",
                    _ => "topstories"
                };

                var idsJson = await httpClient.GetStringAsync($"{BaseUrl}/{endpoint}.json");
                var ids = JsonSerializer.Deserialize(idsJson, ApiJsonContext.Default.Int64Array);

                if (ids == null) continue;

                // Throttle concurrent fetches to 5 at a time
                var tasks = ids
                    .Where(id => !seen.Contains(id))
                    .Take(config.MaxStories)
                    .Select(async id =>
                    {
                        await Throttle.WaitAsync();
                        try
                        {
                            var storyJson = await httpClient.GetStringAsync($"{BaseUrl}/item/{id}.json");
                            return JsonSerializer.Deserialize(storyJson, ApiJsonContext.Default.HnStory);
                        }
                        catch
                        {
                            return null;
                        }
                        finally
                        {
                            Throttle.Release();
                        }
                    });

                var stories = await Task.WhenAll(tasks);

                foreach (var story in stories.Where(s => s != null && s.Score >= config.MinScore))
                {
                    if (seen.Add(story!.Id))
                    {
                        items.Add(new ContentItem
                        {
                            Id = $"hn_{story.Id}",
                            Source = "hn",
                            Title = story.Title ?? "Untitled",
                            Url = story.Url,
                            Content = story.Text,
                            Author = story.By,
                            Score = story.Score,
                            CommentCount = story.Descendants,
                            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(story.Time)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to fetch HN {section}: {ex.Message}");
            }

            if (items.Count >= limit) break;
        }

        return items.Take(limit).ToList();
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches questions and answers from StackOverflow API.
/// Supports tag-based queries, hot questions, and search.
/// </summary>
public partial class StackOverflowFetcher
{
    private const string ApiBase = "https://api.stackexchange.com/2.3";

    /// <summary>
    /// Fetch hot questions from StackOverflow.
    /// </summary>
    public async Task<List<ContentItem>> FetchHotAsync(int limit = 25)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{ApiBase}/questions?order=desc&sort=hot&site=stackoverflow&pagesize={limit}&filter=withbody";
            var json = await FetchCompressedAsync(url);
            var response = JsonSerializer.Deserialize(json, SoJsonContext.Default.SoResponse);

            if (response?.Items != null)
            {
                foreach (var q in response.Items)
                {
                    items.Add(ToContentItem(q));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to fetch SO hot questions: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Fetch questions by tag (e.g., "c#", "dotnet", "python").
    /// </summary>
    public async Task<List<ContentItem>> FetchByTagAsync(string tag, int limit = 25, string sort = "votes")
    {
        var items = new List<ContentItem>();

        try
        {
            var encodedTag = Uri.EscapeDataString(tag);
            var url = $"{ApiBase}/questions?order=desc&sort={sort}&tagged={encodedTag}&site=stackoverflow&pagesize={limit}&filter=withbody";
            var json = await FetchCompressedAsync(url);
            var response = JsonSerializer.Deserialize(json, SoJsonContext.Default.SoResponse);

            if (response?.Items != null)
            {
                foreach (var q in response.Items)
                {
                    items.Add(ToContentItem(q));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to fetch SO tag '{tag}': {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Search StackOverflow for a query.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int limit = 25)
    {
        var items = new List<ContentItem>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{ApiBase}/search/advanced?order=desc&sort=relevance&q={encodedQuery}&site=stackoverflow&pagesize={limit}&filter=withbody";
            var json = await FetchCompressedAsync(url);
            var response = JsonSerializer.Deserialize(json, SoJsonContext.Default.SoResponse);

            if (response?.Items != null)
            {
                foreach (var q in response.Items)
                {
                    items.Add(ToContentItem(q));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to search SO for '{query}': {ex.Message}");
        }

        return items;
    }

    private async Task<string> FetchCompressedAsync(string url)
    {
        // StackOverflow API always returns gzip-compressed responses
        // Use a handler with automatic decompression
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0 (github.com/scottgal/lucidrag)");

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    private static ContentItem ToContentItem(SoQuestion q)
    {
        // Strip HTML tags from body
        var body = q.Body != null ? StripHtml(q.Body) : null;
        var tags = q.Tags ?? [];

        return new ContentItem
        {
            Id = $"so_{q.QuestionId}",
            Source = "stackoverflow",
            Title = WebUtility.HtmlDecode(q.Title ?? "Untitled"),
            Url = q.Link,
            Content = body,
            Author = q.Owner?.DisplayName,
            Score = q.Score,
            CommentCount = q.AnswerCount,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(q.CreationDate),
            Tags = tags.ToList()
        };
    }

    private static string StripHtml(string html)
    {
        // Simple HTML tag stripper - decode entities and remove tags
        var text = HtmlTagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > 2000 ? text[..2000] : text;
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

// StackOverflow API models
public record SoResponse
{
    [JsonPropertyName("items")] public List<SoQuestion>? Items { get; init; }
    [JsonPropertyName("has_more")] public bool HasMore { get; init; }
    [JsonPropertyName("quota_remaining")] public int QuotaRemaining { get; init; }
}

public record SoQuestion
{
    [JsonPropertyName("question_id")] public long QuestionId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("answer_count")] public int AnswerCount { get; init; }
    [JsonPropertyName("view_count")] public int ViewCount { get; init; }
    [JsonPropertyName("creation_date")] public long CreationDate { get; init; }
    [JsonPropertyName("tags")] public string[]? Tags { get; init; }
    [JsonPropertyName("owner")] public SoOwner? Owner { get; init; }
}

public record SoOwner
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("reputation")] public int Reputation { get; init; }
}

[JsonSerializable(typeof(SoResponse))]
[JsonSerializable(typeof(SoQuestion))]
[JsonSerializable(typeof(SoOwner))]
public partial class SoJsonContext : JsonSerializerContext;

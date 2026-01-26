using System.Web;
using AngleSharp;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// DuckDuckGo search integration - no API key needed
/// </summary>
public class DuckDuckGoSearch
{
    private readonly HttpClient _httpClient;

    public DuckDuckGoSearch(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Search DuckDuckGo and return results
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int maxResults = 10, Action<string>? progress = null)
    {
        var items = new List<ContentItem>();

        progress?.Invoke($"Searching DuckDuckGo for: {query}");

        try
        {
            // Use DuckDuckGo HTML search (no API needed)
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = $"https://html.duckduckgo.com/html/?q={encodedQuery}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();

            // Parse results from HTML
            items = await ParseSearchResultsAsync(html, maxResults);

            progress?.Invoke($"Found {items.Count} search results");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: DuckDuckGo search failed: {ex.Message}[/]");
        }

        return items;
    }

    private static async Task<List<ContentItem>> ParseSearchResultsAsync(string html, int maxResults)
    {
        var items = new List<ContentItem>();

        // Parse DuckDuckGo HTML results
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html));

        // Get organic results (skip ads with .result--ad class)
        var results = document.QuerySelectorAll(".result:not(.result--ad)");

        foreach (var result in results.Take(maxResults))
        {
            var titleLink = result.QuerySelector(".result__a");
            var snippetEl = result.QuerySelector(".result__snippet");

            var title = titleLink?.TextContent?.Trim();
            var href = titleLink?.GetAttribute("href");
            var snippet = snippetEl?.TextContent?.Trim();

            if (string.IsNullOrEmpty(title)) continue;

            // DuckDuckGo wraps URLs - extract the actual URL
            var url = ExtractUrl(href);
            if (string.IsNullOrEmpty(url)) continue;

            // Skip ad tracking URLs
            if (url.Contains("duckduckgo.com/y.js") || url.Contains("/aclick?")) continue;

            items.Add(new ContentItem
            {
                Id = $"ddg_{GenerateId(url)}",
                Source = "search",
                Title = title,
                Url = url,
                Content = snippet,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        return items;
    }

    private static string? ExtractUrl(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;

        // DuckDuckGo uses redirect URLs like //duckduckgo.com/l/?uddg=...
        if (href.Contains("uddg="))
        {
            var uddgStart = href.IndexOf("uddg=") + 5;
            var uddgEnd = href.IndexOf('&', uddgStart);
            if (uddgEnd == -1) uddgEnd = href.Length;
            var encoded = href[uddgStart..uddgEnd];
            return HttpUtility.UrlDecode(encoded);
        }

        // Direct URL
        if (href.StartsWith("http"))
            return href;

        return null;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }
}

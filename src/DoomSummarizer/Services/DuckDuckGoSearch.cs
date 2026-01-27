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
    /// Search DuckDuckGo and return results.
    /// Tries the HTML endpoint first, falls back to the lite endpoint.
    /// Both endpoints may block bot-like requests (CAPTCHA / timeout).
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int maxResults = 10, Action<string>? progress = null)
    {
        var items = new List<ContentItem>();

        progress?.Invoke($"Searching DuckDuckGo for: {query}");

        // Try HTML endpoint first, then lite endpoint
        string[] endpoints =
        [
            $"https://html.duckduckgo.com/html/?q={HttpUtility.UrlEncode(query)}",
            $"https://lite.duckduckgo.com/lite/?q={HttpUtility.UrlEncode(query)}"
        ];

        foreach (var url in endpoints)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

                var response = await _httpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync(cts.Token);

                // Detect CAPTCHA / bot-block pages
                if (html.Contains("bots use DuckDuckGo", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("challenge/", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine($"[yellow]DuckDuckGo returned CAPTCHA for {new Uri(url).Host}[/]");
                    continue;
                }

                items = await ParseSearchResultsAsync(html, maxResults);

                if (items.Count > 0)
                {
                    progress?.Invoke($"Found {items.Count} search results");
                    return items;
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[yellow]DuckDuckGo timed out ({new Uri(url).Host})[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]DuckDuckGo failed ({new Uri(url).Host}): {ex.Message}[/]");
            }
        }

        if (items.Count == 0)
            AnsiConsole.MarkupLine("[yellow]DuckDuckGo: all endpoints blocked or timed out — skipping[/]");

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

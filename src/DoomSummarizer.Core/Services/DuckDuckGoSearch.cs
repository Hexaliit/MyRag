using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using AngleSharp;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     DuckDuckGo search integration - no API key needed.
///     Uses circuit breaker and rate limiter to avoid hammering DDG after CAPTCHA blocks.
/// </summary>
public class DuckDuckGoSearch
{
    private const string ServiceName = "duckduckgo";
    private readonly CircuitBreakerService? _circuit;
    private readonly HttpClient _httpClient;

    public DuckDuckGoSearch(HttpClient httpClient, CircuitBreakerService? circuit = null)
    {
        _httpClient = httpClient;
        _circuit = circuit;
    }

    /// <summary>
    ///     Search DuckDuckGo and return results.
    ///     Tries the HTML endpoint first, falls back to the lite endpoint.
    ///     Both endpoints may block bot-like requests (CAPTCHA / timeout).
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int maxResults = 10, Action<string>? progress = null)
    {
        var items = new List<ContentItem>();

        // Pre-flight: skip if circuit is open
        if (_circuit != null && !await _circuit.IsServiceAvailableAsync(ServiceName))
        {
            Debug.WriteLine("DuckDuckGo: circuit open -- skipping");
            return items;
        }

        progress?.Invoke($"Searching DuckDuckGo for: {query}");

        // Try HTML endpoint first, then lite endpoint
        string[] endpoints =
        [
            $"https://html.duckduckgo.com/html/?q={HttpUtility.UrlEncode(query)}",
            $"https://lite.duckduckgo.com/lite/?q={HttpUtility.UrlEncode(query)}"
        ];

        foreach (var url in endpoints)
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                var response = await ApiRateLimiter.ExecuteAsync(ServiceName, async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                    return await _httpClient.SendAsync(request, ct);
                }, cts.Token);

                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync(cts.Token);

                // Detect CAPTCHA / bot-block pages
                if (html.Contains("bots use DuckDuckGo", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("challenge/", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"DuckDuckGo returned CAPTCHA for {new Uri(url).Host}");
                    if (_circuit != null)
                        await _circuit.ReportFailureAsync(ServiceName, CircuitFailureType.RateLimit, "CAPTCHA block");
                    continue;
                }

                items = await ParseSearchResultsAsync(html, maxResults);

                if (items.Count > 0)
                {
                    if (_circuit != null)
                        await _circuit.ReportSuccessAsync(ServiceName);

                    // Enrich search results with full article content (bounded parallel)
                    // Skip homepage URLs (no path) — SmartReader can't extract articles from them
                    var toEnrich = items
                        .Where(i => !string.IsNullOrEmpty(i.Url)
                                    && (string.IsNullOrEmpty(i.Content) || i.Content.Length < 200)
                                    && HasArticlePath(i.Url!))
                        .ToList();

                    if (toEnrich.Count > 0)
                    {
                        using var enrichSemaphore = new SemaphoreSlim(5);
                        var linkEnrichTasks = toEnrich
                            .Select(async i =>
                            {
                                await enrichSemaphore.WaitAsync();
                                try
                                {
                                    var content = await ContentItemHelpers.FetchLinkContentAsync(_httpClient, i.Url!);
                                    if (content != null)
                                    {
                                        i.Content = content;
                                        i.IsEnriched = true;
                                    }
                                }
                                finally { enrichSemaphore.Release(); }
                            })
                            .ToList();
                        await Task.WhenAll(linkEnrichTasks);
                    }

                    progress?.Invoke($"Found {items.Count} search results");
                    return items;
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"DuckDuckGo timed out ({new Uri(url).Host})");
                if (_circuit != null)
                    await _circuit.ReportFailureAsync(ServiceName, CircuitFailureType.ServerError, "Timeout");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DuckDuckGo failed ({new Uri(url).Host}): {ex.Message}");
                if (_circuit != null)
                    await _circuit.ReportFailureAsync(ServiceName, CircuitFailureType.ServerError, ex.Message);
            }

        if (items.Count == 0)
            Debug.WriteLine("DuckDuckGo: all endpoints blocked or timed out -- skipping");

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

    /// <summary>
    ///     Returns true if the URL has a meaningful path (not just a homepage).
    ///     SmartReader can't extract articles from homepages, so skip them.
    /// </summary>
    private static bool HasArticlePath(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.TrimEnd('/');
            // Root path or single-segment paths like /news are index pages
            return path.Length > 1 && path.Count(c => c == '/') >= 2;
        }
        catch
        {
            return false;
        }
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
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }
}
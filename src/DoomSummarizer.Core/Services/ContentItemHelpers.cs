using System.Diagnostics;
using DoomSummarizer.Models;
using SmartReader;

namespace DoomSummarizer.Services;

/// <summary>
///     Shared helpers for content item lookups and link content fetching.
///     Eliminates duplication across fetchers and synthesis code.
/// </summary>
public static class ContentItemHelpers
{
    /// <summary>
    ///     Build O(1) lookup dictionaries for ContentItems by URL and Title.
    ///     Returns (null, null) when input is null/empty.
    /// </summary>
    public static (Dictionary<string, ContentItem>? byUrl, Dictionary<string, ContentItem>? byTitle)
        BuildContentLookups(List<ContentItem>? contentItems)
    {
        if (contentItems is not { Count: > 0 })
            return (null, null);

        var byUrl = new Dictionary<string, ContentItem>(contentItems.Count, StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, ContentItem>(contentItems.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in contentItems)
        {
            if (c.Url != null) byUrl.TryAdd(c.Url, c);
            if (c.Title != null) byTitle.TryAdd(c.Title, c);
        }

        return (byUrl, byTitle);
    }

    /// <summary>
    ///     Resolve a ContentItem by URL first, then title fallback.
    /// </summary>
    public static ContentItem? Resolve(
        Dictionary<string, ContentItem>? byUrl,
        Dictionary<string, ContentItem>? byTitle,
        string? url,
        string? title)
    {
        ContentItem? item = null;
        if (byUrl != null && url != null) byUrl.TryGetValue(url, out item);
        if (item == null && byTitle != null && title != null) byTitle.TryGetValue(title, out item);
        return item;
    }

    /// <summary>
    ///     Fetch article content from a URL using SmartReader.
    ///     Returns null on failure (non-fatal). Used by HN and Reddit fetchers
    ///     to enrich link posts that lack inline content.
    /// </summary>
    public static async Task<string?> FetchLinkContentAsync(HttpClient httpClient, string url,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DoomSummarizer/1.0");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml");

            using var response = await httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(html)) return null;

            var reader = new Reader(url, html);
            var article = await reader.GetArticleAsync();
            if (article is { IsReadable: true } && !string.IsNullOrEmpty(article.TextContent))
                return article.TextContent;
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation from the caller's token
            if (ct.IsCancellationRequested) throw;
            Debug.WriteLine($"Link content fetch timed out for {url}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Link content fetch failed for {url}: {ex.Message}");
        }

        return null;
    }
}

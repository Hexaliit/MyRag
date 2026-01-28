using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DoomSummarizer.Services;

/// <summary>
/// Fixes broken/redirect URLs from aggregator sources like Google News.
/// Google News RSS returns redirect URLs (news.google.com/rss/articles/...) that often:
/// - Return 400 errors
/// - Require JavaScript to resolve
/// - Have tracking parameters
///
/// This service resolves these to canonical article URLs.
/// </summary>
public partial class UrlFixerService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UrlFixerService>? _logger;

    // Cache resolved URLs to avoid repeated lookups
    private readonly Dictionary<string, string> _urlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public UrlFixerService(HttpClient httpClient, ILogger<UrlFixerService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Check if a URL needs fixing (is from a problematic source).
    /// </summary>
    public static bool NeedsFix(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        return url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("google.com/url?", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("bing.com/news/apiclick", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempt to fix/resolve a URL to its canonical form.
    /// Returns the original URL if fixing fails.
    /// </summary>
    public async Task<string> FixUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return url;

        // Check cache first
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_urlCache.TryGetValue(url, out var cached))
                return cached;
        }
        finally
        {
            _cacheLock.Release();
        }

        var resolved = url;
        try
        {
            if (IsGoogleNewsUrl(url))
                resolved = await ResolveGoogleNewsUrlAsync(url, ct);
            else if (IsGoogleRedirectUrl(url))
                resolved = ExtractGoogleRedirectTarget(url) ?? url;
            else if (IsBingNewsUrl(url))
                resolved = await ResolveBingNewsUrlAsync(url, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve URL: {Url}", url);
        }

        // Cache the result
        await _cacheLock.WaitAsync(ct);
        try
        {
            _urlCache[url] = resolved;
        }
        finally
        {
            _cacheLock.Release();
        }

        return resolved;
    }

    /// <summary>
    /// Fix URLs in batch (parallel with throttling).
    /// </summary>
    public async Task<Dictionary<string, string>> FixUrlsBatchAsync(
        IEnumerable<string> urls, CancellationToken ct = default)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var urlsToFix = urls.Where(NeedsFix).Distinct().ToList();

        if (urlsToFix.Count == 0)
        {
            foreach (var url in urls)
                results[url] = url;
            return results;
        }

        // Parallel with throttling (max 5 concurrent)
        using var semaphore = new SemaphoreSlim(5);
        var tasks = urlsToFix.Select(async url =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var resolved = await FixUrlAsync(url, ct);
                return (original: url, resolved);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var resolved = await Task.WhenAll(tasks);
        foreach (var (original, fixedUrl) in resolved)
            results[original] = fixedUrl;

        // Add unchanged URLs
        foreach (var url in urls.Where(u => !NeedsFix(u)))
            results[url] = url;

        return results;
    }

    // --- Detection ---

    private static bool IsGoogleNewsUrl(string url) =>
        url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase) &&
        (url.Contains("/rss/articles/", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("/articles/", StringComparison.OrdinalIgnoreCase));

    private static bool IsGoogleRedirectUrl(string url) =>
        url.Contains("google.com/url?", StringComparison.OrdinalIgnoreCase);

    private static bool IsBingNewsUrl(string url) =>
        url.Contains("bing.com/news/apiclick", StringComparison.OrdinalIgnoreCase);

    // --- Resolution ---

    /// <summary>
    /// Resolve Google News article URL.
    /// Google News encodes the actual URL in a base64 blob within the URL path.
    /// Format: /articles/CBMi[base64]... where base64 contains the actual URL.
    ///
    /// Algorithm based on: https://gist.github.com/huksley/bc3cb046157a99cd9d1517b32f91a99e
    /// </summary>
    private async Task<string> ResolveGoogleNewsUrlAsync(string url, CancellationToken ct)
    {
        // Method 1: Try base64 decoding (no network request needed)
        var decoded = TryDecodeGoogleNewsBase64(url);
        if (decoded != null)
            return decoded;

        // Method 2: Follow redirects with HttpClient (fallback)
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);

            // Check for redirect
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.Found)
            {
                var location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location) && !location.Contains("news.google.com"))
                {
                    if (!location.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseUri = new Uri(url);
                        location = new Uri(baseUri, location).ToString();
                    }
                    return location;
                }
            }

            // Check HTML for canonical/og:url
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);

                var canonicalMatch = CanonicalLinkPattern().Match(content);
                if (canonicalMatch.Success)
                {
                    var canonical = canonicalMatch.Groups[1].Value;
                    if (!canonical.Contains("news.google.com") && Uri.IsWellFormedUriString(canonical, UriKind.Absolute))
                        return canonical;
                }

                var ogUrlMatch = OgUrlPattern().Match(content);
                if (ogUrlMatch.Success)
                {
                    var ogUrl = ogUrlMatch.Groups[1].Value;
                    if (!ogUrl.Contains("news.google.com") && Uri.IsWellFormedUriString(ogUrl, UriKind.Absolute))
                        return ogUrl;
                }
            }
        }
        catch (HttpRequestException)
        {
            // Network error
        }
        catch (TaskCanceledException)
        {
            // Timeout
        }

        return url;
    }

    /// <summary>
    /// Try to decode Google News URL using base64 extraction.
    /// Google News URLs contain base64-encoded data with embedded article URLs.
    ///
    /// Format: The base64 blob starts with CBMi (old) or AU_yqL (new).
    /// Inside the decoded data: prefix bytes + length + URL bytes + suffix
    /// </summary>
    private static string? TryDecodeGoogleNewsBase64(string url)
    {
        try
        {
            // Extract the article ID from the URL path
            // URLs look like: https://news.google.com/rss/articles/CBMiXWh0dHBz...
            // or: https://news.google.com/articles/CBMiXWh0dHBz...

            var uri = new Uri(url);
            var path = uri.AbsolutePath;

            // Find the base64 encoded part (starts with CBMi for old style)
            var articleMatch = GoogleNewsArticleIdPattern().Match(path);
            if (!articleMatch.Success)
                return null;

            var encodedId = articleMatch.Groups[1].Value;

            // Google uses URL-safe base64 (- instead of +, _ instead of /)
            var base64 = encodedId
                .Replace('-', '+')
                .Replace('_', '/');

            // Pad to multiple of 4
            var padding = (4 - base64.Length % 4) % 4;
            base64 += new string('=', padding);

            var decoded = Convert.FromBase64String(base64);

            // Old format: prefix [0x08, 0x13, 0x22] then varint length, then URL
            // Try to find URL by looking for "http" in the decoded bytes
            var decodedStr = System.Text.Encoding.UTF8.GetString(decoded);

            // Look for http:// or https:// in the decoded data
            var httpIdx = decodedStr.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            var httpsIdx = decodedStr.IndexOf("https://", StringComparison.OrdinalIgnoreCase);

            var startIdx = (httpIdx >= 0 && httpsIdx >= 0)
                ? Math.Min(httpIdx, httpsIdx)
                : Math.Max(httpIdx, httpsIdx);

            if (startIdx < 0)
                return null;

            // Extract URL - it ends at the first non-URL character or another URL
            var urlPart = decodedStr[startIdx..];

            // Find end of URL (typically followed by garbage or another URL)
            // URLs end at whitespace, null bytes, or control characters
            var endIdx = urlPart.Length;
            for (var i = 0; i < urlPart.Length; i++)
            {
                var c = urlPart[i];
                if (c < 32 || c == 127) // Control character or DEL
                {
                    endIdx = i;
                    break;
                }
            }

            var extractedUrl = urlPart[..endIdx].Trim();

            // Validate it's a proper URL
            if (Uri.IsWellFormedUriString(extractedUrl, UriKind.Absolute) &&
                !extractedUrl.Contains("news.google.com"))
            {
                return extractedUrl;
            }
        }
        catch
        {
            // Decode failed
        }

        return null;
    }

    [GeneratedRegex(@"/(?:rss/)?articles/([A-Za-z0-9_-]+)")]
    private static partial Regex GoogleNewsArticleIdPattern();

    /// <summary>
    /// Extract target URL from Google redirect URL (google.com/url?q=...).
    /// </summary>
    private static string? ExtractGoogleRedirectTarget(string url)
    {
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var target = query["q"] ?? query["url"];
            if (!string.IsNullOrEmpty(target) && Uri.IsWellFormedUriString(target, UriKind.Absolute))
                return target;
        }
        catch
        {
            // Parse error
        }
        return null;
    }

    /// <summary>
    /// Resolve Bing News click URL by following redirect.
    /// </summary>
    private async Task<string> ResolveBingNewsUrlAsync(string url, CancellationToken ct)
    {
        // Bing encodes the URL in a 'url' parameter
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var target = query["url"];
            if (!string.IsNullOrEmpty(target))
            {
                var decoded = System.Web.HttpUtility.UrlDecode(target);
                if (Uri.IsWellFormedUriString(decoded, UriKind.Absolute))
                    return decoded;
            }

            // Fallback: follow redirect
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.Headers.Location != null)
                return response.Headers.Location.ToString();
        }
        catch
        {
            // Error - return original
        }

        return url;
    }

    // --- Regex patterns ---

    [GeneratedRegex(@"<link[^>]+rel=[""']canonical[""'][^>]+href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalLinkPattern();

    [GeneratedRegex(@"<meta[^>]+http-equiv=[""']refresh[""'][^>]+content=[""']\d+;\s*url=([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshPattern();

    [GeneratedRegex(@"<meta[^>]+property=[""']og:url[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgUrlPattern();
}

/// <summary>
/// Extension methods for URL fixing integration.
/// </summary>
public static class UrlFixerExtensions
{
    /// <summary>
    /// Fix URLs in a list of ContentItems in-place.
    /// </summary>
    public static async Task FixUrlsAsync(
        this UrlFixerService fixer,
        IList<Models.ContentItem> items,
        CancellationToken ct = default)
    {
        var urlsToFix = items
            .Where(i => UrlFixerService.NeedsFix(i.Url))
            .Select(i => i.Url!)
            .Distinct()
            .ToList();

        if (urlsToFix.Count == 0) return;

        var resolved = await fixer.FixUrlsBatchAsync(urlsToFix, ct);

        foreach (var item in items)
        {
            if (item.Url != null && resolved.TryGetValue(item.Url, out var fixedUrl))
                item.Url = fixedUrl;
        }
    }
}

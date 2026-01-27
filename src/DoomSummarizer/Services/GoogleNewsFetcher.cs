using System.Web;
using System.Xml.Linq;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Google News RSS search - no API key required.
/// Supports full-text search with time filtering.
/// Primary source for non-tech topic queries.
/// </summary>
public class GoogleNewsFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://news.google.com/rss/search";

    /// <summary>
    /// Search Google News via RSS feed. Returns real news articles from major outlets.
    /// </summary>
    public async Task<List<ContentItem>> SearchAsync(string query, int maxResults = 20, int? daysBack = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var q = query;
            if (daysBack.HasValue)
                q += $" when:{daysBack}d";

            var encodedQuery = HttpUtility.UrlEncode(q);
            var url = $"{BaseUrl}?q={encodedQuery}&hl=en-US&gl=US&ceid=US:en";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MostlyLucid-DoomSummarizer/1.0");
            request.Headers.Add("Accept", "application/rss+xml, application/xml, text/xml");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            var rssItems = doc.Descendants("item");
            foreach (var item in rssItems.Take(maxResults))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var description = item.Element("description")?.Value;
                var pubDate = item.Element("pubDate")?.Value;
                var source = item.Element("source")?.Value;

                if (string.IsNullOrEmpty(title)) continue;

                // Google News links are redirects - extract the actual URL
                var actualUrl = ExtractActualUrl(link);

                items.Add(new ContentItem
                {
                    Id = $"gnews_{GenerateId(actualUrl ?? link ?? title)}",
                    Source = "gnews",
                    Title = System.Net.WebUtility.HtmlDecode(title),
                    Url = actualUrl ?? link,
                    Content = StripHtml(description ?? ""),
                    Author = source,
                    CreatedAt = TryParseDate(pubDate)
                });
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Google News search failed: {ex.Message}[/]");
        }

        // Resolve Google News redirect URLs to actual article URLs
        await ResolveRedirectUrlsAsync(items);

        return items;
    }

    /// <summary>
    /// Fetch a Google News topic feed (HEALTH, SCIENCE, BUSINESS, etc.)
    /// </summary>
    public async Task<List<ContentItem>> FetchTopicAsync(string topic, int maxResults = 20)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"https://news.google.com/rss/headlines/section/topic/{topic.ToUpperInvariant()}?hl=en-US&gl=US&ceid=US:en";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MostlyLucid-DoomSummarizer/1.0");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            foreach (var item in doc.Descendants("item").Take(maxResults))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var description = item.Element("description")?.Value;
                var pubDate = item.Element("pubDate")?.Value;
                var source = item.Element("source")?.Value;

                if (string.IsNullOrEmpty(title)) continue;

                var actualUrl = ExtractActualUrl(link);

                items.Add(new ContentItem
                {
                    Id = $"gnews_{GenerateId(actualUrl ?? link ?? title)}",
                    Source = "gnews",
                    Title = System.Net.WebUtility.HtmlDecode(title),
                    Url = actualUrl ?? link,
                    Content = StripHtml(description ?? ""),
                    Author = source,
                    CreatedAt = TryParseDate(pubDate)
                });
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Google News topic '{topic}' feed failed, falling back to search: {ex.Message}[/]");
            // Fall back to keyword search — topic feeds can be unreliable
            // (SearchAsync already resolves redirect URLs)
            return await SearchAsync(topic.ToLowerInvariant().Replace("_", " "), maxResults, daysBack: 7);
        }

        // Resolve Google News redirect URLs to actual article URLs
        await ResolveRedirectUrlsAsync(items);

        return items;
    }

    /// <summary>
    /// Google News topic names that map to RSS topic feeds.
    /// </summary>
    public static readonly Dictionary<string, string> TopicMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["health"] = "HEALTH",
        ["science"] = "SCIENCE",
        ["business"] = "BUSINESS",
        ["technology"] = "TECHNOLOGY",
        ["entertainment"] = "ENTERTAINMENT",
        ["sports"] = "SPORTS",
        ["world"] = "WORLD",
        ["nation"] = "NATION"
    };

    /// <summary>
    /// Google News RSS provides redirect URLs (news.google.com/rss/articles/...).
    /// Tries offline base64/protobuf decoding first; returns original URL as fallback
    /// for HTTP redirect resolution in ResolveRedirectUrlsAsync.
    /// </summary>
    private static string? ExtractActualUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return DecodeGoogleNewsUrl(url) ?? url;
    }

    /// <summary>
    /// Decode a Google News redirect URL by extracting the real article URL from
    /// the base64-encoded protobuf payload in /articles/{encoded} or /rss/articles/{encoded}.
    /// Returns null if decoding fails (falls through to HTTP redirect resolution).
    /// </summary>
    internal static string? DecodeGoogleNewsUrl(string googleUrl)
    {
        // Find the base64 payload after /articles/
        var articlesIdx = googleUrl.IndexOf("/articles/", StringComparison.OrdinalIgnoreCase);
        if (articlesIdx < 0) return null;

        var encoded = googleUrl[(articlesIdx + "/articles/".Length)..];

        // Strip query string (?oc=5 etc.)
        var queryIdx = encoded.IndexOf('?');
        if (queryIdx >= 0) encoded = encoded[..queryIdx];

        if (encoded.Length < 10) return null;

        // Convert base64url to standard base64
        encoded = encoded.Replace('-', '+').Replace('_', '/');
        var padding = (4 - encoded.Length % 4) % 4;
        if (padding < 4) encoded += new string('=', padding);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch { return null; }

        // Strategy 1: Protobuf-aware parsing
        // The payload is typically: 0x08 (field 1, varint) + value + 0x22 (field 4, length-delimited) + URL
        var url = TryExtractProtobufUrl(bytes);
        if (url != null) return url;

        // Strategy 2: Scan for "http" in decoded bytes (handles unknown protobuf layouts)
        url = TryExtractUrlByScanning(bytes);
        return url;
    }

    /// <summary>
    /// Parse the protobuf structure to extract the URL string field.
    /// Expected layout: field 1 (varint) + field 4 (length-delimited string = URL).
    /// </summary>
    private static string? TryExtractProtobufUrl(byte[] bytes)
    {
        try
        {
            var offset = 0;

            // Skip field 1: tag 0x08 + varint value
            if (offset < bytes.Length && bytes[offset] == 0x08)
            {
                offset++;
                // Skip varint
                while (offset < bytes.Length && (bytes[offset] & 0x80) != 0) offset++;
                if (offset < bytes.Length) offset++; // Final varint byte
            }

            // Expect field 4: tag 0x22 (field 4, wire type 2 = length-delimited)
            if (offset >= bytes.Length || bytes[offset] != 0x22) return null;
            offset++;

            // Read varint length
            var len = ReadVarint(bytes, ref offset);
            if (len <= 0 || offset + len > bytes.Length) return null;

            var candidate = System.Text.Encoding.UTF8.GetString(bytes, offset, len);
            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(candidate, UriKind.Absolute, out _) ? candidate : null;
            }
        }
        catch
        {
            // Protobuf parsing failed
        }

        return null;
    }

    /// <summary>
    /// Fallback: scan decoded bytes for "http://" or "https://" and extract the URL.
    /// Handles cases where the protobuf structure doesn't match our expected layout.
    /// </summary>
    private static string? TryExtractUrlByScanning(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        string? bestUrl = null;

        var searchPos = 0;
        while (searchPos < text.Length)
        {
            var httpIdx = text.IndexOf("http", searchPos, StringComparison.OrdinalIgnoreCase);
            if (httpIdx < 0) break;

            // Extract URL: all printable ASCII chars until we hit a non-URL char
            var end = httpIdx;
            while (end < text.Length && IsUrlChar(text[end]))
                end++;

            var candidate = text[httpIdx..end];
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && !uri.Host.Contains("google.com", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the longest non-Google URL (the article URL is typically longer)
                if (bestUrl == null || candidate.Length > bestUrl.Length)
                    bestUrl = candidate;
            }

            searchPos = end;
        }

        return bestUrl;
    }

    private static bool IsUrlChar(char c) =>
        c > 0x20 && c < 0x7F && c != '"' && c != '\'' && c != '<' && c != '>' && c != ' ';

    private static int ReadVarint(byte[] bytes, ref int offset)
    {
        var result = 0;
        var shift = 0;
        while (offset < bytes.Length)
        {
            var b = bytes[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    /// <summary>
    /// Resolve any remaining Google News redirect URLs.
    /// Strategy 1: Follow HTTP 3xx redirects (fast, works for older format).
    /// Strategy 2: Fetch HTML body and parse for article URL (newer JS-redirect format).
    /// </summary>
    private async Task ResolveRedirectUrlsAsync(List<ContentItem> items)
    {
        var googleItems = items
            .Where(i => i.Url != null && i.Url.Contains("news.google.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (googleItems.Count == 0) return;

        // Use a separate HttpClient with cookie support for redirect resolution.
        // Google News requires cookies to resolve article URLs; without them,
        // redirects land on policies.google.com/technologies/cookies.
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        using var redirectClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        using var semaphore = new SemaphoreSlim(5);
        var tasks = googleItems.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                request.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

                // Fetch full body (needed for JS-redirect pages)
                using var response = await redirectClient.SendAsync(request, cts.Token);

                // Strategy 1: Check if HTTP redirect resolved it
                var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
                if (IsValidArticleUrl(finalUrl))
                {
                    item.Url = finalUrl;
                    return;
                }

                // Strategy 2: Parse HTML body for the article URL
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cts.Token);
                    var extractedUrl = ExtractUrlFromGoogleNewsHtml(html);
                    if (extractedUrl != null)
                    {
                        item.Url = extractedUrl;
                    }
                }
            }
            catch
            {
                // URL resolution failure is non-fatal — keep original Google News URL
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Extract the actual article URL from a Google News redirect page's HTML.
    /// Google News uses JavaScript redirects, but the target URL often appears in:
    /// - data-n-au attributes
    /// - <a> tags with article links
    /// - <script> blocks with window.location or redirect URLs
    /// - <noscript> sections with direct links
    /// </summary>
    private static string? ExtractUrlFromGoogleNewsHtml(string html)
    {
        // Pattern 1: data-n-au attribute (Google News article URL)
        var dataNauMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"data-n-au=""([^""]+)""");
        if (dataNauMatch.Success && IsValidArticleUrl(dataNauMatch.Groups[1].Value))
            return System.Net.WebUtility.HtmlDecode(dataNauMatch.Groups[1].Value);

        // Pattern 2: <a href="..."> with class containing "article" or rel="noopener"
        var anchorMatches = System.Text.RegularExpressions.Regex.Matches(
            html, @"<a[^>]+href=""(https?://[^""]+)""[^>]*>");
        foreach (System.Text.RegularExpressions.Match match in anchorMatches)
        {
            var href = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            if (IsValidArticleUrl(href))
                return href;
        }

        // Pattern 3: JavaScript redirect (window.location)
        var jsMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"window\.location\s*[=.]\s*['""]?(https?://[^'"";\s]+)");
        if (jsMatch.Success && IsValidArticleUrl(jsMatch.Groups[1].Value))
            return jsMatch.Groups[1].Value;

        // Pattern 4: meta refresh redirect
        var metaMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"<meta[^>]+http-equiv=['""]refresh['""][^>]+url=(https?://[^""'>;\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (metaMatch.Success && IsValidArticleUrl(metaMatch.Groups[1].Value))
            return metaMatch.Groups[1].Value;

        return null;
    }

    private static bool IsValidArticleUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && !url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("policies.google.com", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("google.com/sorry", StringComparison.OrdinalIgnoreCase)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 1500 ? text[..1500] : text;
    }

    private static DateTimeOffset TryParseDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return DateTimeOffset.UtcNow;
        return DateTimeOffset.TryParse(dateStr, out var result) ? result : DateTimeOffset.UtcNow;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }
}

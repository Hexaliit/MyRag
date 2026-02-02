using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Google News RSS search - no API key required.
/// Supports full-text search with time filtering.
/// Primary source for non-tech topic queries.
/// </summary>
public partial class GoogleNewsFetcher(HttpClient httpClient)
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
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)");
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
            // Don't use AnsiConsole here — this runs from background tasks during Progress rendering
            // and AnsiConsole is not thread-safe, causing IndexOutOfRange crashes
            System.Diagnostics.Debug.WriteLine($"Google News search failed: {ex.Message}");
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
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)");

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
            // Don't use AnsiConsole here — this runs from background tasks during Progress rendering
            System.Diagnostics.Debug.WriteLine($"Google News topic '{topic}' feed failed, falling back to search: {ex.Message}");
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
    /// Resolve any remaining Google News redirect URLs using three strategies:
    /// Strategy 1: batchexecute API (works for new "AU_yqL" format, post-July 2024).
    /// Strategy 2: HTTP 3xx redirect following (works for older format).
    /// Strategy 3: HTML body parsing for JS-redirect pages.
    /// </summary>
    private async Task ResolveRedirectUrlsAsync(List<ContentItem> items)
    {
        var googleItems = items
            .Where(i => i.Url != null && i.Url.Contains("news.google.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (googleItems.Count == 0) return;

        // Strategy 1: Try batchexecute API for all unresolved items
        await ResolveBatchExecuteAsync(googleItems);

        // Find items still unresolved after batchexecute
        var stillUnresolved = googleItems
            .Where(i => i.Url != null && i.Url.Contains("news.google.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (stillUnresolved.Count == 0) return;

        // Strategy 2 & 3: HTTP redirect + HTML parsing fallback
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        using var redirectClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        using var semaphore = new SemaphoreSlim(5);
        var tasks = stillUnresolved.Select(async item =>
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

                using var response = await redirectClient.SendAsync(request, cts.Token);

                // Check if HTTP redirect resolved it
                var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
                if (IsValidArticleUrl(finalUrl))
                {
                    item.Url = finalUrl;
                    return;
                }

                // Parse HTML body for the article URL
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
    /// Resolve Google News URLs via the batchexecute API (DotsSplashUi endpoint).
    /// This handles the new "AU_yqL" URL format introduced in July 2024 where
    /// the article URL is no longer embedded in the base64 payload.
    /// </summary>
    private async Task ResolveBatchExecuteAsync(List<ContentItem> items)
    {
        const string batchUrl = "https://news.google.com/_/DotsSplashUi/data/batchexecute";
        const int maxConcurrent = 3; // Conservative — Google rate-limits aggressively

        using var semaphore = new SemaphoreSlim(maxConcurrent);
        var resolved = 0;
        var failed = 0;

        var tasks = items.Select(async item =>
        {
            if (item.Url == null) return;

            var articleId = ExtractArticleId(item.Url);
            if (articleId == null) return;

            await semaphore.WaitAsync();
            try
            {
                // Small delay to avoid rate limiting
                if (resolved + failed > 0)
                    await Task.Delay(150);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                // Build the garturlreq RPC payload
                var innerPayload = $"""["garturlreq",[["en-US","US",["FINANCE_TOP_INDICES","WEB_TEST_1_0_0"]],"{articleId}"]]""";
                var outerPayload = $"""[[["Fbv4je",{System.Text.Json.JsonSerializer.Serialize(innerPayload)},"generic"]]]""";
                var formData = $"f.req={HttpUtility.UrlEncode(outerPayload)}";

                var request = new HttpRequestMessage(HttpMethod.Post, batchUrl)
                {
                    Content = new StringContent(formData, System.Text.Encoding.UTF8,
                        "application/x-www-form-urlencoded")
                };
                request.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

                using var response = await httpClient.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(cts.Token);
                var decodedUrl = ExtractUrlFromBatchResponse(body);
                if (decodedUrl != null && IsValidArticleUrl(decodedUrl))
                {
                    item.Url = decodedUrl;
                    Interlocked.Increment(ref resolved);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Don't use AnsiConsole here — this runs from background tasks during Progress rendering
        if (resolved > 0 || failed > 0)
            System.Diagnostics.Debug.WriteLine($"Google News URL resolution: {resolved} resolved, {failed} failed (batchexecute)");
    }

    /// <summary>
    /// Extract the article ID from a Google News URL.
    /// URL format: https://news.google.com/rss/articles/{ARTICLE_ID}?oc=5
    /// </summary>
    private static string? ExtractArticleId(string url)
    {
        var idx = url.IndexOf("/articles/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var id = url[(idx + "/articles/".Length)..];
        var queryIdx = id.IndexOf('?');
        if (queryIdx >= 0) id = id[..queryIdx];

        return id.Length >= 10 ? id : null;
    }

    /// <summary>
    /// Parse the batchexecute response for the decoded article URL.
    /// Response format is a multi-line structure with nested JSON arrays.
    /// The URL appears in a "garturlres" response after the RPC ID "Fbv4je".
    /// </summary>
    private static string? ExtractUrlFromBatchResponse(string body)
    {
        // The response contains lines of JSON arrays. The URL is typically in:
        // [["wrb.fr","Fbv4je","[\"garturlres\",...]",...]]
        // We need to find a line containing "garturlres" and extract the URL.
        try
        {
            // Find the garturlres payload — it's in a nested JSON string
            var marker = "garturlres";
            var idx = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            // Scan forward for an https:// URL in the response
            var searchRegion = body[idx..Math.Min(idx + 2000, body.Length)];
            var urlMatch = BatchUrlRegex().Match(searchRegion);
            if (urlMatch.Success && IsValidArticleUrl(urlMatch.Value))
                return urlMatch.Value;

            // Alternative: look for escaped URL in the JSON string
            var escapedMatch = BatchEscapedUrlRegex().Match(searchRegion);
            if (escapedMatch.Success)
            {
                var unescaped = escapedMatch.Value.Replace("\\/", "/");
                if (IsValidArticleUrl(unescaped))
                    return unescaped;
            }
        }
        catch
        {
            // Parse failure is non-fatal
        }

        return null;
    }

    /// <summary>
    /// Extract the actual article URL from a Google News redirect page's HTML.
    /// Google News uses JavaScript redirects, but the target URL often appears in:
    /// - data-n-au attributes
    /// - data-n-au attributes in anchor tags
    /// - anchor tags with article links
    /// - script blocks with window.location or redirect URLs
    /// - noscript sections with direct links
    /// </summary>
    private static string? ExtractUrlFromGoogleNewsHtml(string html)
    {
        // Pattern 1: data-n-au attribute (Google News article URL)
        var dataNauMatch = DataNauRegex().Match(html);
        if (dataNauMatch.Success && IsValidArticleUrl(dataNauMatch.Groups[1].Value))
            return System.Net.WebUtility.HtmlDecode(dataNauMatch.Groups[1].Value);

        // Pattern 2: <a href="..."> with class containing "article" or rel="noopener"
        var anchorMatches = AnchorHrefRegex().Matches(html);
        foreach (Match match in anchorMatches)
        {
            var href = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            if (IsValidArticleUrl(href))
                return href;
        }

        // Pattern 3: JavaScript redirect (window.location)
        var jsMatch = WindowLocationRegex().Match(html);
        if (jsMatch.Success && IsValidArticleUrl(jsMatch.Groups[1].Value))
            return jsMatch.Groups[1].Value;

        // Pattern 4: meta refresh redirect
        var metaMatch = MetaRefreshRegex().Match(html);
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
        var text = HtmlTagRegex().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
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

    [GeneratedRegex(@"https?://[^""\\]+")]
    private static partial Regex BatchUrlRegex();

    [GeneratedRegex(@"https?:\\/\\/[^""]+")]
    private static partial Regex BatchEscapedUrlRegex();

    [GeneratedRegex(@"data-n-au=""([^""]+)""")]
    private static partial Regex DataNauRegex();

    [GeneratedRegex(@"<a[^>]+href=""(https?://[^""]+)""[^>]*>")]
    private static partial Regex AnchorHrefRegex();

    [GeneratedRegex(@"window\.location\s*[=.]\s*['""]?(https?://[^'"";\s]+)")]
    private static partial Regex WindowLocationRegex();

    [GeneratedRegex(@"<meta[^>]+http-equiv=['""]refresh['""][^>]+url=(https?://[^""'>;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

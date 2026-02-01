using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Polly;
using Polly.Retry;

namespace DoomSummarizer.Services;

/// <summary>
/// BFS web crawler scoped to a single domain (or domain pattern).
/// Extracts content from each page using SmartReader (ContentExtractor),
/// stores as ContentItems for knowledge base use.
/// Supports HTTP conditional requests (ETag / If-Modified-Since) for
/// bandwidth-efficient incremental re-crawling.
///
/// GitHub raw mode: fetches raw markdown from raw.githubusercontent.com,
/// extracts markdown links, and uses the raw content directly.
///
/// Adaptive rate limiting:
/// - Tracks response time via exponential moving average (EMA)
/// - Scales delay up when server is slow (>2s average), down when fast
/// - Uses Polly retry pipeline for 429/503 with Retry-After header support
/// - Hard limits enforced: delay floor 200ms, ceiling 10s, max concurrency 5
/// </summary>
public class WebCrawlerService
{
    private readonly HttpClient _httpClient;
    private readonly ContentExtractor _extractor;
    private readonly CrawlConfig _config;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public int PagesVisited { get; private set; }
    public int PagesExtracted { get; private set; }
    public int PagesSkipped { get; private set; }
    public int PagesNotModified { get; private set; }
    public int RetryCount { get; private set; }
    public int AdaptiveDelayMs { get; private set; }

    // Adaptive delay state
    private const int DelayFloorMs = 200;
    private const int DelayCeilingMs = 10_000;
    private const int MaxRetryAfterSeconds = 60;
    private const double EmaAlpha = 0.3;
    private double _responseTimeEma;
    private int _currentDelayMs;

    public WebCrawlerService(HttpClient httpClient, CrawlConfig config)
    {
        _httpClient = httpClient;
        _extractor = new ContentExtractor(httpClient);
        _config = config;
        _retryPipeline = BuildRetryPipeline();
    }

    private ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline()
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.TooManyRequests
                        or HttpStatusCode.ServiceUnavailable),
                DelayGenerator = args =>
                {
                    // Respect Retry-After header when present
                    var response = args.Outcome.Result;
                    if (response != null)
                    {
                        var retryAfterMs = ParseRetryAfter(response);
                        if (retryAfterMs.HasValue)
                        {
                            var capped = Math.Min(retryAfterMs.Value, MaxRetryAfterSeconds * 1000);
                            return new ValueTask<TimeSpan?>(TimeSpan.FromMilliseconds(capped));
                        }
                    }
                    // Fall back to Polly's built-in exponential backoff
                    return new ValueTask<TimeSpan?>((TimeSpan?)null);
                },
                OnRetry = args =>
                {
                    RetryCount++;
                    // Double the adaptive delay on each retry to slow down subsequent requests
                    _currentDelayMs = Math.Clamp(_currentDelayMs * 2, DelayFloorMs, DelayCeilingMs);
                    AdaptiveDelayMs = _currentDelayMs;
                    return default;
                }
            })
            .Build();
    }

    /// <summary>
    /// Crawl starting from seedUrl, extracting all same-domain pages.
    /// Returns CrawlResults for each page — either new content or a 304 Not Modified signal.
    /// </summary>
    /// <param name="seedUrl">Starting URL for the crawl.</param>
    /// <param name="cacheProvider">Optional lookup for stored ETag / Last-Modified headers
    /// to enable conditional HTTP requests (If-None-Match / If-Modified-Since).</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="onActivity">Activity description callback.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<CrawlResult> CrawlAsync(
        string seedUrl,
        Func<string, (string? etag, string? lastModified)>? cacheProvider = null,
        IProgress<(int visited, int queued, int extracted)>? progress = null,
        Action<string>? onActivity = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Enforce hard limits on configuration
        var effectiveDelayMs = Math.Max(_config.DelayMs, DelayFloorMs);
        var effectiveConcurrency = Math.Clamp(_config.MaxConcurrency, 1, 5);
        var effectiveTimeout = Math.Clamp(_config.TimeoutSeconds, 5, 60);

        _currentDelayMs = effectiveDelayMs;
        AdaptiveDelayMs = _currentDelayMs;

        // GitHub raw mode: convert seed URL to raw.githubusercontent.com
        if (_config.GitHubRawMode)
        {
            var rawUrl = ConvertToRawGitHubUrl(seedUrl);
            if (rawUrl != null) seedUrl = rawUrl;
        }

        var seedUri = new Uri(seedUrl);
        // Allow both www and non-www variants of the domain
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            seedUri.Host,
            seedUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? seedUri.Host[4..]
                : "www." + seedUri.Host
        };

        // BFS frontier: (url, depth)
        var frontier = new Queue<(string url, int depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        frontier.Enqueue((NormalizeUrl(seedUrl), 0));

        using var semaphore = new SemaphoreSlim(effectiveConcurrency);

        while (frontier.Count > 0 && !ct.IsCancellationRequested)
        {
            var (url, depth) = frontier.Dequeue();

            if (visited.Contains(url)) continue;
            visited.Add(url);

            if (visited.Count > _config.MaxPages) break;

            PagesVisited++;
            progress?.Report((PagesVisited, frontier.Count, PagesExtracted));

            // Adaptive politeness delay
            if (_currentDelayMs > 0 && PagesVisited > 1)
                await Task.Delay(_currentDelayMs, ct);

            onActivity?.Invoke($"Crawling: {TruncateUrl(url, 60)}");

            // Stage 1: Fetch page (with conditional request headers for cache validation)
            string? body = null;
            string? finalUrl = url;
            string? responseETag = null;
            string? responseLastModified = null;
            bool wasNotModified = false;
            bool isMarkdown = false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MostlyLucid-DoomSummarizer/1.0 (KnowledgeBase)");
                request.Headers.Add("Accept", _config.GitHubRawMode
                    ? "text/plain,text/markdown,text/html,application/xhtml+xml"
                    : "text/html,application/xhtml+xml");

                // Send conditional headers if we have cached ETags / Last-Modified
                if (cacheProvider != null)
                {
                    var (cachedEtag, cachedLastModified) = cacheProvider(url);
                    if (!string.IsNullOrEmpty(cachedEtag))
                        request.Headers.TryAddWithoutValidation("If-None-Match", cachedEtag);
                    if (!string.IsNullOrEmpty(cachedLastModified))
                        request.Headers.TryAddWithoutValidation("If-Modified-Since", cachedLastModified);
                }

                // Measure response time for adaptive delay, execute through Polly retry pipeline
                var sw = Stopwatch.StartNew();
                using var response = await _retryPipeline.ExecuteAsync(
                    async token => await _httpClient.SendAsync(request, token),
                    cts.Token);
                sw.Stop();

                // Update response time EMA and adapt delay
                UpdateAdaptiveDelay(sw.ElapsedMilliseconds, effectiveDelayMs, onActivity);

                // Track redirect — add the redirected host to allowed hosts
                if (response.RequestMessage?.RequestUri is { } redirectUri)
                {
                    finalUrl = redirectUri.AbsoluteUri;
                    allowedHosts.Add(redirectUri.Host);
                    if (redirectUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                        allowedHosts.Add(redirectUri.Host[4..]);
                    else
                        allowedHosts.Add("www." + redirectUri.Host);
                }

                // Capture ETag and Last-Modified from response for cache storage
                responseETag = response.Headers.ETag?.ToString();
                if (response.Content.Headers.LastModified is { } lm)
                    responseLastModified = lm.ToString("R"); // RFC 1123 format

                // If still 429/503 after all retries exhausted, skip this page
                if (response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable)
                {
                    onActivity?.Invoke($"Rate limited ({(int)response.StatusCode}) — retries exhausted, skipping");
                    PagesSkipped++;
                    continue;
                }

                // 304 Not Modified — content unchanged, skip processing
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    PagesNotModified++;
                    wasNotModified = true;
                }
                else if (!response.IsSuccessStatusCode)
                {
                    PagesSkipped++;
                    continue;
                }
                else
                {
                    // Successful response after previous backoff — gradually restore delay
                    if (_currentDelayMs > effectiveDelayMs)
                    {
                        _currentDelayMs = Math.Max(effectiveDelayMs, (int)(_currentDelayMs * 0.75));
                        AdaptiveDelayMs = _currentDelayMs;
                    }

                    // Determine content type
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

                    // In GitHub raw mode, accept text/plain for .md files
                    isMarkdown = _config.GitHubRawMode && !isHtml &&
                        (contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
                         || contentType.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase)
                         || IsMarkdownUrl(url));

                    if (!isHtml && !isMarkdown)
                    {
                        PagesSkipped++;
                        continue;
                    }

                    body = await response.Content.ReadAsStringAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch
            {
                PagesSkipped++;
                continue;
            }

            // Yield 304 Not Modified result (no content to extract, but caller needs the URL)
            if (wasNotModified)
            {
                yield return new CrawlResult
                {
                    Url = finalUrl,
                    NotModified = true,
                    ETag = responseETag,
                    LastModified = responseLastModified
                };
                continue;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                PagesSkipped++;
                continue;
            }

            // ── GitHub raw markdown path ────────────────────────────────────
            if (isMarkdown)
            {
                bool shouldIndexMd = MatchesPathFilter(finalUrl);

                // Extract markdown links for BFS (only follow .md/.markdown files)
                if (depth < _config.MaxDepth)
                {
                    var links = ExtractMarkdownLinks(body, finalUrl, allowedHosts);
                    foreach (var link in links)
                    {
                        var normalized = NormalizeUrl(link);
                        if (!visited.Contains(normalized) && !IsBlockedExtension(normalized))
                            frontier.Enqueue((normalized, depth + 1));
                    }
                }

                if (!shouldIndexMd || body.Length < 50)
                {
                    PagesSkipped++;
                    continue;
                }

                // Chunk the markdown into heading-based sections for focused embeddings.
                // Uses the shared DocumentChunker from the DocSummarizer pipeline —
                // one chunking strategy for all document types.
                var mdTitle = ExtractMarkdownTitle(body, finalUrl);
                var parentId = $"crawl_{GenerateId(finalUrl)}";
                var displayUrl = ConvertToGitHubBlobUrl(finalUrl) ?? finalUrl;
                var chunker = new DocumentChunker();
                var chunks = chunker.ChunkByStructure(body);

                foreach (var chunk in chunks)
                {
                    PagesExtracted++;
                    progress?.Report((PagesVisited, frontier.Count, PagesExtracted));

                    yield return new CrawlResult
                    {
                        Item = new ContentItem
                        {
                            Id = chunks.Count > 1
                                ? $"{parentId}_{chunk.Order}"
                                : parentId,
                            Source = $"crawl:{_config.Name}",
                            Title = !string.IsNullOrEmpty(chunk.Heading) ? chunk.Heading : mdTitle,
                            Url = displayUrl,
                            Content = chunk.Content,
                            ParentDocumentId = chunks.Count > 1 ? parentId : null,
                            ChunkSequence = chunk.Order,
                            UnitLevel = chunks.Count > 1 ? "section" : "document",
                            FetchedAt = DateTimeOffset.UtcNow,
                            CreatedAt = DateTimeOffset.UtcNow
                        },
                        Url = finalUrl,
                        ETag = responseETag,
                        LastModified = responseLastModified
                    };
                }
                continue;
            }

            // ── Standard HTML path ──────────────────────────────────────────
            // Stage 2: Extract content
            // When ContentScopedLinks is enabled, we need the article HTML for link discovery,
            // so content extraction runs before link extraction.
            bool shouldIndex = MatchesPathFilter(finalUrl);
            ExtractedContent? extracted = null;

            if (_config.ContentScopedLinks || shouldIndex)
            {
                try
                {
                    extracted = _extractor.ExtractFromHtml(body, finalUrl);
                }
                catch
                {
                    // Content extraction failed — fall through
                }
            }

            bool hasGoodContent = extracted is { IsReadable: true } && extracted.Content.Length >= 50;

            // Stage 3: Extract links for BFS discovery
            if (depth < _config.MaxDepth)
            {
                // When content-scoped: use article HTML so we only follow links the author
                // put in the content (not navigation/sidebar/footer links from the page chrome).
                // Falls back to full HTML if content extraction failed.
                var linkHtml = _config.ContentScopedLinks && hasGoodContent && extracted!.HtmlContent != null
                    ? extracted.HtmlContent
                    : body;

                // In content-scoped mode with no usable content, skip link discovery entirely
                // (the page has no article content, so there are no meaningful links to follow)
                if (!_config.ContentScopedLinks || hasGoodContent)
                {
                    var links = ExtractSameDomainLinks(linkHtml, finalUrl, allowedHosts);
                    foreach (var link in links)
                    {
                        var normalized = NormalizeUrl(link);
                        if (!visited.Contains(normalized) && !IsBlockedExtension(normalized))
                            frontier.Enqueue((normalized, depth + 1));
                    }
                }
            }

            // Apply path filter — pages outside the filter are crawled for links but not indexed
            if (!shouldIndex || !hasGoodContent)
            {
                PagesSkipped++;
                continue;
            }

            PagesExtracted++;
            progress?.Report((PagesVisited, frontier.Count, PagesExtracted));

            yield return new CrawlResult
            {
                Item = new ContentItem
                {
                    Id = $"crawl_{GenerateId(finalUrl)}",
                    Source = $"crawl:{_config.Name}",
                    Title = extracted!.Title ?? finalUrl ?? url,
                    Url = finalUrl,
                    Content = extracted.BestContent,
                    Author = extracted.Author,
                    CreatedAt = extracted.PublishedDate is { } pd
                        ? new DateTimeOffset(DateTime.SpecifyKind(pd, DateTimeKind.Utc))
                        : DateTimeOffset.UtcNow,
                    FetchedAt = DateTimeOffset.UtcNow,
                    ContentStructure = extracted.Structure
                },
                Url = finalUrl!,
                ETag = responseETag,
                LastModified = responseLastModified
            };
        }
    }

    // ─── GitHub raw helpers ─────────────────────────────────────────────

    /// <summary>
    /// Convert a github.com blob URL to a raw.githubusercontent.com URL.
    /// github.com/{owner}/{repo}/blob/{branch}/{path}
    ///   → raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
    /// </summary>
    internal static string? ConvertToRawGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // Expected: {owner}/{repo}/blob/{branch}/{path...}
        if (segments.Length < 5) return null;
        if (!segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase)
            && !segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
            return null;

        var owner = segments[0];
        var repo = segments[1];
        // segments[2] = blob/tree (skip)
        var branch = segments[3];
        var path = string.Join('/', segments[4..]);

        return $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}";
    }

    /// <summary>
    /// Convert a raw.githubusercontent.com URL back to a github.com blob URL for display.
    /// raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
    ///   → github.com/{owner}/{repo}/blob/{branch}/{path}
    /// </summary>
    private static string? ConvertToGitHubBlobUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // Expected: {owner}/{repo}/{branch}/{path...}
        if (segments.Length < 4) return null;

        var owner = segments[0];
        var repo = segments[1];
        var branch = segments[2];
        var path = string.Join('/', segments[3..]);

        return $"https://github.com/{owner}/{repo}/blob/{branch}/{path}";
    }

    /// <summary>
    /// Extract links from raw markdown content. Only follows .md/.markdown files.
    /// Handles inline links [text](url) and reference-style [ref]: url definitions.
    /// </summary>
    private static List<string> ExtractMarkdownLinks(string markdown, string baseUrl, HashSet<string> allowedHosts)
    {
        var links = new List<string>();

        // Inline links: [text](url) but NOT image links ![alt](url)
        foreach (Match m in Regex.Matches(markdown, @"(?<!!)\[[^\]]+\]\(([^)\s]+)"))
        {
            TryAddMarkdownLink(m.Groups[1].Value, baseUrl, allowedHosts, links);
        }

        // Reference-style link definitions: [ref]: url
        foreach (Match m in Regex.Matches(markdown, @"^\[[^\]]+\]:\s*(\S+)", RegexOptions.Multiline))
        {
            TryAddMarkdownLink(m.Groups[1].Value, baseUrl, allowedHosts, links);
        }

        return links;
    }

    private static void TryAddMarkdownLink(string href, string baseUrl, HashSet<string> allowedHosts, List<string> links)
    {
        if (string.IsNullOrWhiteSpace(href)) return;
        if (href.StartsWith('#')) return; // fragment-only

        // Strip title portion if present: url "title"
        var spaceIdx = href.IndexOf(' ');
        if (spaceIdx > 0) href = href[..spaceIdx];

        // Strip surrounding angle brackets: <url>
        href = href.Trim('<', '>');

        // Only follow markdown files
        if (!IsMarkdownUrl(href)) return;

        try
        {
            var absoluteUri = new Uri(new Uri(baseUrl), href);

            // Convert absolute GitHub blob URLs to raw.githubusercontent.com so they
            // stay on the allowed host (the seed was converted to raw at the start).
            if (absoluteUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var rawUrl = ConvertToRawGitHubUrl(absoluteUri.AbsoluteUri);
                if (rawUrl != null)
                    absoluteUri = new Uri(rawUrl);
            }

            if (allowedHosts.Contains(absoluteUri.Host) && absoluteUri.Scheme is "http" or "https")
                links.Add(absoluteUri.AbsoluteUri);
        }
        catch
        {
            // Invalid URL — skip
        }
    }

    private static bool IsMarkdownUrl(string url)
    {
        // Strip fragment and query for extension check
        var path = url.Split('#')[0].Split('?')[0];
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extract a title from raw markdown: first # heading, or filename from URL.
    /// </summary>
    private static string ExtractMarkdownTitle(string markdown, string url)
    {
        var match = Regex.Match(markdown, @"^#\s+(.+)$", RegexOptions.Multiline);
        if (match.Success) return match.Groups[1].Value.Trim();

        // Fall back to filename
        try
        {
            var path = new Uri(url).AbsolutePath;
            var filename = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(filename) ? "Untitled" : filename;
        }
        catch
        {
            return "Untitled";
        }
    }

    // ─── Standard HTML helpers ──────────────────────────────────────────

    /// <summary>
    /// Update the adaptive delay based on observed response time.
    /// Uses exponential moving average (EMA) to smooth response time tracking.
    /// When the server is slow (EMA > 2s), delay scales up proportionally.
    /// On fast responses, delay gradually returns toward the configured minimum.
    /// </summary>
    private void UpdateAdaptiveDelay(long responseTimeMs, int configuredDelayMs, Action<string>? onActivity)
    {
        // Update EMA: first request seeds the value, subsequent requests blend
        _responseTimeEma = _responseTimeEma == 0
            ? responseTimeMs
            : (_responseTimeEma * (1 - EmaAlpha)) + (responseTimeMs * EmaAlpha);

        // Calculate adaptive delay: max(configured, responseTimeEma * 1.5), clamped to limits
        var adaptiveTarget = (int)Math.Max(configuredDelayMs, _responseTimeEma * 1.5);
        var newDelay = Math.Clamp(adaptiveTarget, DelayFloorMs, DelayCeilingMs);

        // Only update if the change is meaningful (>50ms difference avoids jitter)
        if (Math.Abs(newDelay - _currentDelayMs) > 50)
        {
            var direction = newDelay > _currentDelayMs ? "up" : "down";
            onActivity?.Invoke($"Adaptive delay {direction}: {_currentDelayMs}ms → {newDelay}ms (EMA: {_responseTimeEma:F0}ms)");
            _currentDelayMs = newDelay;
            AdaptiveDelayMs = _currentDelayMs;
        }
    }

    /// <summary>
    /// Parse the Retry-After header from an HTTP response.
    /// Supports both delay-seconds and HTTP-date formats (RFC 7231 §7.1.3).
    /// Returns delay in milliseconds, or null if the header is absent/unparsable.
    /// </summary>
    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null) return null;

        // Delay-seconds format (e.g., "Retry-After: 120")
        if (retryAfter.Delta is { } delta)
        {
            var ms = (int)Math.Min(delta.TotalMilliseconds, MaxRetryAfterSeconds * 1000);
            return Math.Max(ms, DelayFloorMs);
        }

        // HTTP-date format (e.g., "Retry-After: Thu, 30 Jan 2026 12:00:00 GMT")
        if (retryAfter.Date is { } date)
        {
            var waitMs = (int)(date - DateTimeOffset.UtcNow).TotalMilliseconds;
            return waitMs > 0
                ? Math.Clamp(waitMs, DelayFloorMs, MaxRetryAfterSeconds * 1000)
                : DelayFloorMs;
        }

        return null;
    }

    /// <summary>
    /// Extract all same-domain links from HTML.
    /// </summary>
    private static List<string> ExtractSameDomainLinks(string html, string baseUrl, HashSet<string> allowedHosts)
    {
        var links = new List<string>();
        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(req => req.Content(html).Address(baseUrl))
                .GetAwaiter().GetResult();

            foreach (var anchor in document.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;

                // Skip fragment-only links, javascript:, mailto:, tel:
                if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var absoluteUri = new Uri(new Uri(baseUrl), href);
                    // Same-domain check (allows www and non-www variants)
                    if (allowedHosts.Contains(absoluteUri.Host)
                        && absoluteUri.Scheme is "http" or "https")
                    {
                        links.Add(absoluteUri.AbsoluteUri);
                    }
                }
                catch
                {
                    // Invalid URL — skip
                }
            }
        }
        catch
        {
            // HTML parsing failure — return empty
        }

        return links;
    }

    /// <summary>
    /// Check if a URL path matches the configured glob filter.
    /// Supports: /blog/* (prefix), *.html (suffix), /docs/*/intro (wildcard segment).
    /// Null filter matches everything.
    /// </summary>
    private bool MatchesPathFilter(string url)
    {
        if (string.IsNullOrEmpty(_config.PathFilter)) return true;

        try
        {
            var path = new Uri(url).AbsolutePath;
            var pattern = _config.PathFilter;

            // Simple glob: convert to regex
            // * matches any sequence of non-/ characters
            // ** matches any sequence including /
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*") + "$";

            return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsBlockedExtension(string url)
    {
        var path = new Uri(url).AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".pdf") || path.EndsWith(".zip") || path.EndsWith(".tar")
            || path.EndsWith(".gz") || path.EndsWith(".exe") || path.EndsWith(".dmg")
            || path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg")
            || path.EndsWith(".gif") || path.EndsWith(".svg") || path.EndsWith(".webp")
            || path.EndsWith(".mp3") || path.EndsWith(".mp4") || path.EndsWith(".mov")
            || path.EndsWith(".css") || path.EndsWith(".js") || path.EndsWith(".xml")
            || path.EndsWith(".json") || path.EndsWith(".rss") || path.EndsWith(".atom");
    }

    private static string NormalizeUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            // Strip fragment and query. Preserve path case (raw.githubusercontent.com is case-sensitive).
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
        }
        catch
        {
            return url.Split('#')[0].Split('?')[0].TrimEnd('/');
        }
    }

    private static string TruncateUrl(string url, int maxLen)
    {
        if (url.Length <= maxLen) return url;
        return url[..(maxLen - 3)] + "...";
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
    }
}

/// <summary>
/// Result of crawling a single page — either new/changed content or a 304 Not Modified signal.
/// </summary>
public record CrawlResult
{
    /// <summary>Extracted content item. Null when NotModified is true.</summary>
    public ContentItem? Item { get; init; }

    /// <summary>The final URL (after redirects) for this page.</summary>
    public string Url { get; init; } = "";

    /// <summary>ETag header from the HTTP response (for cache storage).</summary>
    public string? ETag { get; init; }

    /// <summary>Last-Modified header from the HTTP response (for cache storage).</summary>
    public string? LastModified { get; init; }

    /// <summary>True when the server returned 304 Not Modified.</summary>
    public bool NotModified { get; init; }
}

/// <summary>
/// Configuration for the web crawler.
/// Hard limits are enforced at crawl time regardless of configured values:
/// - DelayMs: floor of 200ms (values below are raised to 200ms)
/// - MaxConcurrency: capped at 5 (values above are reduced to 5)
/// - TimeoutSeconds: clamped to range [5, 60]
/// The actual delay between requests adapts dynamically based on server response speed.
/// </summary>
public record CrawlConfig
{
    /// <summary>Name of the knowledge base (used as source prefix).</summary>
    public string Name { get; init; } = "default";

    /// <summary>Maximum crawl depth from seed URL (0 = seed only).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Maximum pages to crawl.</summary>
    public int MaxPages { get; init; } = 200;

    /// <summary>
    /// Minimum delay between requests in milliseconds.
    /// Adaptive delay may increase this based on server response speed.
    /// Hard floor: 200ms (values below are raised automatically).
    /// </summary>
    public int DelayMs { get; init; } = 500;

    /// <summary>
    /// Maximum concurrent requests. Hard cap: 5.
    /// </summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>
    /// Timeout per page in seconds. Clamped to range [5, 60].
    /// </summary>
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>URL path filter (glob pattern, e.g. "/blog/*"). Null means no filter.</summary>
    public string? PathFilter { get; init; }

    /// <summary>
    /// When true, extract links only from the article content area (SmartReader output)
    /// rather than the full page HTML. Useful for sites like GitHub where the page
    /// chrome contains many same-domain navigation links that would overwhelm the crawl.
    /// Default: false (extract links from full page).
    /// </summary>
    public bool ContentScopedLinks { get; init; }

    /// <summary>
    /// When true, convert GitHub blob URLs to raw.githubusercontent.com and fetch raw markdown
    /// instead of rendered HTML. Extracts markdown links to follow other .md files in the repo.
    /// Default: false.
    /// </summary>
    public bool GitHubRawMode { get; init; }
}

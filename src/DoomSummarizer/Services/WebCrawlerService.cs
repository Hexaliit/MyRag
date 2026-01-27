using System.Security.Cryptography;
using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// BFS web crawler scoped to a single domain (or domain pattern).
/// Extracts content from each page using SmartReader (ContentExtractor),
/// stores as ContentItems for knowledge base use.
/// </summary>
public class WebCrawlerService
{
    private readonly HttpClient _httpClient;
    private readonly ContentExtractor _extractor;
    private readonly CrawlConfig _config;

    public int PagesVisited { get; private set; }
    public int PagesExtracted { get; private set; }
    public int PagesSkipped { get; private set; }

    public WebCrawlerService(HttpClient httpClient, CrawlConfig config)
    {
        _httpClient = httpClient;
        _extractor = new ContentExtractor(httpClient);
        _config = config;
    }

    /// <summary>
    /// Crawl starting from seedUrl, extracting all same-domain pages.
    /// Returns ContentItems for each successfully extracted page.
    /// </summary>
    public async IAsyncEnumerable<ContentItem> CrawlAsync(
        string seedUrl,
        IProgress<(int visited, int queued, int extracted)>? progress = null,
        Action<string>? onActivity = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
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

        using var semaphore = new SemaphoreSlim(_config.MaxConcurrency);

        while (frontier.Count > 0 && !ct.IsCancellationRequested)
        {
            var (url, depth) = frontier.Dequeue();

            if (visited.Contains(url)) continue;
            visited.Add(url);

            if (visited.Count > _config.MaxPages) break;

            PagesVisited++;
            progress?.Report((PagesVisited, frontier.Count, PagesExtracted));

            // Politeness delay
            if (_config.DelayMs > 0 && PagesVisited > 1)
                await Task.Delay(_config.DelayMs, ct);

            onActivity?.Invoke($"Crawling: {TruncateUrl(url, 60)}");

            // Stage 1: Fetch HTML
            string? html = null;
            string? finalUrl = url;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MostlyLucid-DoomSummarizer/1.0 (KnowledgeBase)");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml");

                using var response = await _httpClient.SendAsync(request, cts.Token);

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

                if (!response.IsSuccessStatusCode)
                {
                    PagesSkipped++;
                    continue;
                }

                // Only process HTML content
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    PagesSkipped++;
                    continue;
                }

                html = await response.Content.ReadAsStringAsync(cts.Token);
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

            if (string.IsNullOrWhiteSpace(html))
            {
                PagesSkipped++;
                continue;
            }

            // Stage 2: Extract links ALWAYS (even if content extraction fails)
            // This ensures the crawler keeps discovering pages on the domain
            if (depth < _config.MaxDepth)
            {
                var links = ExtractSameDomainLinks(html, finalUrl, allowedHosts);
                foreach (var link in links)
                {
                    var normalized = NormalizeUrl(link);
                    if (!visited.Contains(normalized) && !IsBlockedExtension(normalized))
                        frontier.Enqueue((normalized, depth + 1));
                }
            }

            // Stage 3: Extract content (separate try-catch so link discovery isn't lost)
            // Apply path filter — pages outside the filter are crawled for links but not indexed
            if (!MatchesPathFilter(finalUrl))
            {
                PagesSkipped++;
                continue;
            }

            ExtractedContent? extracted;
            try
            {
                extracted = _extractor.ExtractFromHtml(html, finalUrl);
            }
            catch
            {
                PagesSkipped++;
                continue;
            }

            if (extracted == null || !extracted.IsReadable || extracted.Content.Length < 50)
            {
                PagesSkipped++;
                continue;
            }

            PagesExtracted++;
            progress?.Report((PagesVisited, frontier.Count, PagesExtracted));

            yield return new ContentItem
            {
                Id = $"crawl_{GenerateId(finalUrl)}",
                Source = $"crawl:{_config.Name}",
                Title = extracted.Title,
                Url = finalUrl,
                Content = extracted.BestContent,
                Author = extracted.Author,
                CreatedAt = extracted.PublishedDate is { } pd
                    ? new DateTimeOffset(DateTime.SpecifyKind(pd, DateTimeKind.Utc))
                    : DateTimeOffset.UtcNow,
                FetchedAt = DateTimeOffset.UtcNow,
                ContentStructure = extracted.Structure
            };
        }
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
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(
                path, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            // Remove fragment, normalize path
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/').ToLowerInvariant();
        }
        catch
        {
            return url.Split('#')[0].Split('?')[0].TrimEnd('/').ToLowerInvariant();
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
/// Configuration for the web crawler.
/// </summary>
public record CrawlConfig
{
    /// <summary>Name of the knowledge base (used as source prefix).</summary>
    public string Name { get; init; } = "default";

    /// <summary>Maximum crawl depth from seed URL (0 = seed only).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Maximum pages to crawl.</summary>
    public int MaxPages { get; init; } = 200;

    /// <summary>Delay between requests in milliseconds (politeness).</summary>
    public int DelayMs { get; init; } = 500;

    /// <summary>Maximum concurrent requests.</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>Timeout per page in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>URL path filter (glob pattern, e.g. "/blog/*"). Null means no filter.</summary>
    public string? PathFilter { get; init; }
}

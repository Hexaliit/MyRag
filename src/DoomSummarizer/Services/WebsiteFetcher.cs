using AngleSharp;
using AngleSharp.Dom;
using DoomSummarizer.Models;
using Microsoft.Playwright;
using SmartReader;
using Spectre.Console;

namespace DoomSummarizer.Services;

public class WebsiteFetcher : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _playwrightInitialized;

    public WebsiteFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Extract full article content from a URL using SmartReader (ML-based readability).
    /// </summary>
    public async Task<(string title, string content, string? author, string? image)> ExtractArticleAsync(string url)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DoomSummarizer/1.0");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return ("", "", null, null);

            var html = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(html)) return ("", "", null, null);

            // Use SmartReader for intelligent extraction
            var reader = new Reader(url, html);
            var article = await reader.GetArticleAsync();

            if (article != null && article.IsReadable)
            {
                return (
                    article.Title ?? "",
                    article.TextContent ?? "",
                    article.Author,
                    article.FeaturedImage
                );
            }

            // Fallback to basic extraction
            return FallbackExtractArticle(html, url);
        }
        catch
        {
            return ("", "", null, null);
        }
    }

    private static (string title, string content, string? author, string? image) FallbackExtractArticle(string html, string url)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        var title = document.QuerySelector("title")?.TextContent ??
                    document.QuerySelector("h1")?.TextContent ?? "";

        var content = ExtractMainContent(document);
        var image = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content");

        return (title.Trim(), content, null, image);
    }

    public async Task<List<ContentItem>> FetchAsync(List<WebsiteConfig> websites, Action<string>? progress = null)
    {
        var items = new List<ContentItem>();

        foreach (var site in websites)
        {
            if (string.IsNullOrEmpty(site.Url)) continue;

            progress?.Invoke($"Fetching {site.Url}...");

            try
            {
                string html;
                if (site.UsePlaywright)
                {
                    html = await FetchWithPlaywrightAsync(site.Url);
                }
                else
                {
                    var response = await _httpClient.GetAsync(site.Url);
                    response.EnsureSuccessStatusCode();
                    html = await response.Content.ReadAsStringAsync();
                }

                var articles = await ExtractArticlesAsync(html, site.Url, site.Selector);
                items.AddRange(articles);

                progress?.Invoke($"Extracted {articles.Count} articles from {site.Url}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to fetch {site.Url}: {ex.Message}[/]");
            }
        }

        return items;
    }

    private async Task<string> FetchWithPlaywrightAsync(string url)
    {
        if (!_playwrightInitialized)
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            _playwrightInitialized = true;
        }

        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            return await page.ContentAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<List<ContentItem>> ExtractArticlesAsync(string html, string baseUrl, string? selector)
    {
        var items = new List<ContentItem>();
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html));
        var uri = new Uri(baseUrl);

        // Known site patterns
        if (baseUrl.Contains("news.ycombinator.com"))
        {
            return ExtractHackerNewsStyle(document, baseUrl);
        }

        if (baseUrl.Contains("reddit.com"))
        {
            return ExtractRedditStyle(document, baseUrl);
        }

        // Try custom selector first
        if (!string.IsNullOrEmpty(selector))
        {
            var elements = document.QuerySelectorAll(selector);
            foreach (var el in elements.Take(30))
            {
                var item = ExtractItemFromElement(el, baseUrl);
                if (item != null) items.Add(item);
            }
            if (items.Count > 0) return items;
        }

        // Try to find article elements using common patterns
        var articleElements = FindArticleElements(document);

        if (articleElements.Count >= 2)
        {
            // Multiple articles detected - extract each
            foreach (var article in articleElements.Take(30))
            {
                var item = ExtractItemFromElement(article, baseUrl);
                if (item != null) items.Add(item);
            }
        }
        else
        {
            // Single page - treat as one item
            var title = document.QuerySelector("title")?.TextContent
                        ?? document.QuerySelector("h1")?.TextContent
                        ?? "Untitled";
            var content = ExtractMainContent(document);

            items.Add(new ContentItem
            {
                Id = $"web_{GenerateId(baseUrl)}",
                Source = "web",
                Title = title.Trim(),
                Url = baseUrl,
                Content = content,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        return items;
    }

    private static List<ContentItem> ExtractHackerNewsStyle(IDocument document, string baseUrl)
    {
        var items = new List<ContentItem>();

        // HN uses .titleline for story titles
        var titleLines = document.QuerySelectorAll(".titleline");

        foreach (var titleLine in titleLines.Take(30))
        {
            var link = titleLine.QuerySelector("a");
            if (link == null) continue;

            var title = link.TextContent?.Trim();
            var href = link.GetAttribute("href");

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href)) continue;

            // Resolve URL
            var url = ResolveUrl(baseUrl, href);

            // Try to get score from the corresponding subtext
            var parent = titleLine.ParentElement?.ParentElement;
            var subtext = parent?.NextElementSibling;
            var scoreEl = subtext?.QuerySelector(".score");
            var score = 0;
            if (scoreEl != null)
            {
                var scoreText = scoreEl.TextContent;
                int.TryParse(scoreText?.Replace(" points", "").Replace(" point", ""), out score);
            }

            items.Add(new ContentItem
            {
                Id = $"web_{GenerateId(url)}",
                Source = "web",
                Title = title,
                Url = url,
                Score = score,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        return items;
    }

    private static List<ContentItem> ExtractRedditStyle(IDocument document, string baseUrl)
    {
        var items = new List<ContentItem>();

        // Reddit uses various structures - try common ones
        var posts = document.QuerySelectorAll("[data-testid='post-container'], .Post, .thing.link");

        foreach (var post in posts.Take(30))
        {
            var titleEl = post.QuerySelector("h3, [data-adclicklocation='title'], .title a");
            var title = titleEl?.TextContent?.Trim();

            var link = post.QuerySelector("a[href*='/comments/'], a[data-click-id='body']");
            var href = link?.GetAttribute("href") ?? titleEl?.GetAttribute("href");

            if (string.IsNullOrEmpty(title)) continue;

            var url = string.IsNullOrEmpty(href) ? baseUrl : ResolveUrl(baseUrl, href);

            items.Add(new ContentItem
            {
                Id = $"web_{GenerateId(url)}",
                Source = "web",
                Title = title,
                Url = url,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        return items;
    }

    private static ContentItem? ExtractItemFromElement(IElement element, string baseUrl)
    {
        // Find title - try various patterns
        var titleEl = element.QuerySelector("h1, h2, h3, h4, .title, [class*='title'], [class*='headline']");
        var link = element.QuerySelector("a[href]");

        var title = titleEl?.TextContent?.Trim()
                    ?? link?.TextContent?.Trim();

        if (string.IsNullOrEmpty(title) || title.Length < 5 || title.Length > 500) return null;

        var href = link?.GetAttribute("href");
        var url = string.IsNullOrEmpty(href) ? null : ResolveUrl(baseUrl, href);

        // Skip navigation/footer links
        if (url != null && IsNavigationLink(url, baseUrl)) return null;

        // Get content preview
        var contentEl = element.QuerySelector("p, .summary, .excerpt, .description, [class*='content']");
        var content = contentEl?.TextContent?.Trim();

        return new ContentItem
        {
            Id = $"web_{GenerateId(url ?? baseUrl + title)}",
            Source = "web",
            Title = title,
            Url = url ?? baseUrl,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool IsNavigationLink(string url, string baseUrl)
    {
        var lowerUrl = url.ToLowerInvariant();
        return lowerUrl.Contains("/login") ||
               lowerUrl.Contains("/signup") ||
               lowerUrl.Contains("/register") ||
               lowerUrl.Contains("/about") ||
               lowerUrl.Contains("/contact") ||
               lowerUrl.Contains("/privacy") ||
               lowerUrl.Contains("/terms") ||
               lowerUrl.Contains("/faq") ||
               lowerUrl.Contains("#");
    }

    private static List<IElement> FindArticleElements(IDocument document)
    {
        // Priority ordered selectors for article containers
        var selectors = new[]
        {
            // Semantic elements
            "article",
            "main article",

            // Common class patterns for news/blog sites
            "[class*='story']",
            "[class*='article-item']",
            "[class*='post-item']",
            "[class*='news-item']",
            "[class*='entry']",
            "[class*='card']",

            // List-based layouts
            "ul.posts > li",
            "ul.stories > li",
            "ul.articles > li",
            ".post-list > *",
            ".story-list > *",
            ".article-list > *",
            ".news-list > *",

            // Generic patterns
            "[class*='item']",
            "li[class]"
        };

        foreach (var sel in selectors)
        {
            try
            {
                var elements = document.QuerySelectorAll(sel).ToList();

                // Filter out small elements (likely navigation)
                elements = elements.Where(e =>
                {
                    var text = e.TextContent?.Trim() ?? "";
                    return text.Length > 20 && text.Length < 5000;
                }).ToList();

                if (elements.Count >= 3 && elements.Count <= 100)
                {
                    return elements;
                }
            }
            catch
            {
                // Invalid selector, skip
            }
        }

        return [];
    }

    private static string ExtractMainContent(IDocument document)
    {
        // Remove script, style, nav, footer, aside
        foreach (var element in document.QuerySelectorAll("script, style, nav, footer, aside, header, .sidebar, .menu, .advertisement, .ad, .nav"))
        {
            element.Remove();
        }

        // Try main content selectors
        var mainSelectors = new[] { "main", "article", "[role='main']", ".content", "#content", ".post-content", ".article-content", ".entry-content" };
        foreach (var sel in mainSelectors)
        {
            var main = document.QuerySelector(sel);
            if (main != null && main.TextContent.Length > 100)
            {
                return CleanText(main.TextContent);
            }
        }

        // Fall back to body
        return CleanText(document.Body?.TextContent ?? "");
    }

    private static string CleanText(string text)
    {
        // Remove excessive whitespace
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines).Trim();
    }

    private static string ResolveUrl(string baseUrl, string href)
    {
        if (string.IsNullOrEmpty(href)) return baseUrl;

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (Uri.TryCreate(new Uri(baseUrl), href, out var resolved))
            return resolved.ToString();

        return href;
    }

    private static string GenerateId(string input)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
        }
        _playwright?.Dispose();
    }
}

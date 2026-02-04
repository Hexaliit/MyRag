using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using DoomSummarizer.Models;
using Mostlylucid.Summarizer.Core.Utilities;

namespace DoomSummarizer.Services;

/// <summary>
///     One-hop link following service. Extracts links from article pages and
///     fetches linked content to enrich the parent article with additional context.
///     Uses content hashing + ETags to skip re-processing unchanged pages.
///     When an embedder + query embedding are provided, links are ranked by
///     semantic relevance before fetching (avoids wasting bandwidth on irrelevant links).
/// </summary>
public class LinkFollowingService
{
    private readonly LinkFollowingConfig _config;
    private readonly ContentExtractor _contentExtractor;
    private readonly Func<string, float[]>? _embedder;
    private readonly HttpClient _httpClient;
    private readonly float[]? _queryEmbedding;
    private readonly StorageService? _storage;
    private int _cacheHits;
    private int _linksSkippedByRelevance;
    private int _totalLinksFetched;

    /// <param name="httpClient">HTTP client for fetching linked pages.</param>
    /// <param name="config">Link following configuration.</param>
    /// <param name="storage">Optional storage service for content hash caching.</param>
    /// <param name="embedder">Optional embedding function (e.g. EmbeddingService.Embed) for link relevance pre-filtering.</param>
    /// <param name="queryEmbedding">Pre-computed query/topic embedding to compare link anchor text against.</param>
    public LinkFollowingService(
        HttpClient httpClient,
        LinkFollowingConfig config,
        StorageService? storage = null,
        Func<string, float[]>? embedder = null,
        float[]? queryEmbedding = null)
    {
        _httpClient = httpClient;
        _config = config;
        _contentExtractor = new ContentExtractor(httpClient);
        _storage = storage;
        _embedder = embedder;
        _queryEmbedding = queryEmbedding;
    }

    public int CacheHits => _cacheHits;
    public int LinksSkippedByRelevance => _linksSkippedByRelevance;

    /// <summary>
    ///     Follow links from a batch of content items, enriching each with linked page content.
    ///     Respects MaxTotalLinks across all items.
    /// </summary>
    public async Task FollowLinksAsync(
        List<ContentItem> items,
        IProgress<(int current, int total)>? progress = null,
        Action<string>? onActivity = null,
        CancellationToken ct = default)
    {
        if (!_config.Enabled) return;

        _totalLinksFetched = 0;
        var total = items.Count;
        var completed = 0;

        // Parallel article enrichment with bounded concurrency (4 concurrent fetches)
        using var semaphore = new SemaphoreSlim(4);
        var tasks = items.Select(async (item, i) =>
        {
            if (_totalLinksFetched >= _config.MaxTotalLinks)
            {
                Interlocked.Increment(ref completed);
                progress?.Report((completed, total));
                return;
            }

            if (string.IsNullOrEmpty(item.Url)
                || item.Url.Contains("news.google.com/", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref completed);
                progress?.Report((completed, total));
                return;
            }

            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var host = GetHostName(item.Url);
                onActivity?.Invoke($"Fetching {host}: {Truncate(item.Title, 50)}");

                var linkedPages = await FollowLinksForItemAsync(item, ct);
                item.LinkedPages.AddRange(linkedPages);

                if (item.IsEnriched)
                {
                    var structInfo = item.ContentStructure?.ToSummary() ?? $"{item.Content?.Length ?? 0:N0} chars";
                    onActivity?.Invoke($"[green]Enriched[/] {host} ({structInfo})");
                }

                if (linkedPages.Count > 0)
                    onActivity?.Invoke($"Found {linkedPages.Count} linked pages from {host}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Don't let link following failures break the pipeline
            }
            finally
            {
                semaphore.Release();
                Interlocked.Increment(ref completed);
                progress?.Report((completed, total));
            }
        });

        await Task.WhenAll(tasks);
    }

    private static string GetHostName(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try
        {
            return new Uri(url).Host.Replace("www.", "");
        }
        catch
        {
            return "?";
        }
    }

    private static string Truncate(string text, int maxLen)
    {
        return text.Length > maxLen ? text[..(maxLen - 3)] + "..." : text;
    }

    /// <summary>
    ///     Extract and follow links from a single article page.
    ///     Uses content hashing + ETags to avoid re-processing unchanged content.
    ///     When embedder is available, ranks links by semantic relevance before fetching.
    /// </summary>
    private async Task<List<LinkedPage>> FollowLinksForItemAsync(ContentItem item, CancellationToken ct)
    {
        var results = new List<LinkedPage>();
        var remaining = Math.Min(
            _config.MaxLinksPerArticle,
            _config.MaxTotalLinks - _totalLinksFetched);

        if (remaining <= 0) return results;

        // Check cache: if this URL was fetched recently, skip entirely
        UrlCacheEntry? cacheEntry = null;
        if (_storage != null && !string.IsNullOrEmpty(item.Url))
        {
            cacheEntry = await _storage.GetUrlCacheAsync(item.Url);
            if (cacheEntry != null && (DateTimeOffset.UtcNow - cacheEntry.LastFetched).TotalHours < 4)
            {
                // Content is fresh — skip fetch entirely
                Interlocked.Increment(ref _cacheHits);
                return results;
            }
        }

        // Fetch the article page HTML (with conditional ETag support)
        string? html;
        string? responseETag = null;
        string? responseLastModified = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

            var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml");

            // Conditional fetch: send ETag if we have one from cache
            if (cacheEntry?.ETag != null)
                request.Headers.TryAddWithoutValidation("If-None-Match", cacheEntry.ETag);
            if (cacheEntry?.LastModified != null)
                request.Headers.TryAddWithoutValidation("If-Modified-Since", cacheEntry.LastModified);

            using var response = await _httpClient.SendAsync(request, cts.Token);

            // 304 Not Modified — content unchanged, skip processing
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (_storage != null)
                    await _storage.UpdateUrlCacheAsync(item.Url!, cacheEntry?.ContentHash, cacheEntry?.ETag,
                        cacheEntry?.LastModified, cacheEntry?.ContentLength ?? 0);
                Interlocked.Increment(ref _cacheHits);
                return results;
            }

            if (!response.IsSuccessStatusCode) return results;

            // Capture caching headers from response
            responseETag = response.Headers.ETag?.Tag;
            if (response.Content.Headers.LastModified.HasValue)
                responseLastModified = response.Content.Headers.LastModified.Value.ToString("R");

            html = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch
        {
            return results;
        }

        if (string.IsNullOrWhiteSpace(html)) return results;

        // Enrich the parent item with full article content extracted from the page.
        // Hash only the extracted content area (not full HTML with ads/nav/footer).
        try
        {
            var extracted = _contentExtractor.ExtractFromHtml(html, item.Url!);
            if (extracted is { IsReadable: true } && extracted.BestContent.Length > (item.Content?.Length ?? 0) * 1.5)
            {
                // Check content hash — skip if extracted content hasn't changed
                var contentHash = ContentHasher.ComputeHash(extracted.BestContent);
                if (_storage != null && cacheEntry?.ContentHash == contentHash)
                {
                    // Content area unchanged — update cache timestamp but skip enrichment
                    await _storage.UpdateUrlCacheAsync(item.Url!, contentHash, responseETag, responseLastModified,
                        extracted.BestContent.Length);
                    Interlocked.Increment(ref _cacheHits);
                    return results;
                }

                // Content changed or new — enrich and update cache
                item.Content = extracted.BestContent;
                item.IsEnriched = true;
                item.ContentStructure = extracted.Structure;

                if (_storage != null)
                    await _storage.UpdateUrlCacheAsync(item.Url!, contentHash, responseETag, responseLastModified,
                        extracted.BestContent.Length);
            }
        }
        catch
        {
            // Content enrichment failure is non-fatal
        }

        // Extract links from the article content area (with anchor text + context)
        var candidates = ExtractContentLinks(html, item.Url!);

        // Rank by semantic relevance if embedder is available
        var linksToFollow = RankAndSelectLinks(candidates, remaining);

        // Fetch linked pages in parallel (bounded)
        var fetchTasks = linksToFollow.Select(link => FetchLinkedPageAsync(link.Url, ct));
        var fetched = await Task.WhenAll(fetchTasks);

        foreach (var page in fetched)
            if (page != null)
            {
                results.Add(page);
                Interlocked.Increment(ref _totalLinksFetched);
            }

        return results;
    }

    /// <summary>
    ///     Rank candidate links by a composite score combining:
    ///     1. Query relevance — cosine similarity of link context vs query embedding
    ///     2. Segment salience — positional weighting (inverted pyramid: top links > bottom)
    ///     and paragraph length (longer paragraphs = more substantive content)
    ///     Falls back to positional ordering when no embedder is available.
    /// </summary>
    private List<CandidateLink> RankAndSelectLinks(List<CandidateLink> candidates, int maxLinks)
    {
        if (candidates.Count == 0) return candidates;

        // No embedder or query — fall back to positional ordering
        // (still respects inverted pyramid: first links in article = most important)
        if (_embedder == null || _queryEmbedding == null)
            return candidates.Take(maxLinks).ToList();

        // Score each link on two axes
        var scored = new List<(CandidateLink link, float compositeScore)>();
        foreach (var candidate in candidates)
            try
            {
                // Axis 1: Query relevance (cosine similarity of context vs query)
                var contextEmbed = _embedder(candidate.Context);
                var queryRelevance = CosineSimilarity(_queryEmbedding, contextEmbed);

                // Axis 2: Segment salience
                //   - Position decay: top of article = 1.0, bottom = 0.5 (linear decay)
                var positionScore = 1.0f - candidate.PositionRatio * 0.5f;

                //   - Paragraph substance: longer paragraphs are more likely to contain
                //     real analysis rather than nav/sidebar snippets. Sigmoid-like scaling.
                var substanceScore = Math.Min(1.0f, candidate.ParagraphLength / 300f);

                // Combined segment salience (weighted average of position + substance)
                var salience = positionScore * 0.6f + substanceScore * 0.4f;

                // Final composite: 70% query relevance + 30% segment salience
                var composite = queryRelevance * 0.7f + salience * 0.3f;
                scored.Add((candidate, composite));
            }
            catch
            {
                // Embedding failure — score by salience only
                var positionScore = 1.0f - candidate.PositionRatio * 0.5f;
                scored.Add((candidate, positionScore * 0.3f));
            }

        // Sort by composite score descending
        var sorted = scored
            .OrderByDescending(s => s.compositeScore)
            .ToList();

        // Select top links, skip those below relevance floor
        const float relevanceFloor = 0.15f;
        var selected = new List<CandidateLink>();
        foreach (var (link, score) in sorted)
        {
            if (selected.Count >= maxLinks) break;

            if (score < relevanceFloor)
            {
                Interlocked.Increment(ref _linksSkippedByRelevance);
                continue;
            }

            selected.Add(link);
        }

        return selected;
    }

    /// <summary>
    ///     Extract links from within the main content area of an HTML page.
    ///     Returns structured candidates with anchor text and surrounding paragraph context
    ///     for semantic relevance scoring.
    /// </summary>
    private List<CandidateLink> ExtractContentLinks(string html, string baseUrl)
    {
        var baseUri = new Uri(baseUrl);
        var candidates = new List<CandidateLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add the article's own URL to seen so we don't follow it
        seen.Add(NormalizeUrl(baseUrl));

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).Result;

            // Find the main content area (same heuristic as ContentExtractor fallback)
            var contentSelectors = new[]
            {
                "article", "main", "[role='main']",
                ".post-content", ".article-content", ".entry-content",
                ".content", "#content", ".post", ".article"
            };

            IElement? contentElement = null;
            foreach (var selector in contentSelectors)
            {
                contentElement = document.QuerySelector(selector);
                if (contentElement != null && contentElement.TextContent.Length > 200)
                    break;
            }

            contentElement ??= document.Body;
            if (contentElement == null) return candidates;

            // Extract all <a> tags from within content
            var anchors = contentElement.QuerySelectorAll("a[href]");

            foreach (var anchor in anchors)
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;

                // Skip fragment-only and javascript links
                if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Resolve relative URLs
                if (!Uri.TryCreate(baseUri, href, out var resolvedUri))
                    continue;

                var url = resolvedUri.GetLeftPart(UriPartial.Path);

                // Skip non-HTTP schemes
                if (resolvedUri.Scheme is not ("http" or "https"))
                    continue;

                // Skip blocked extensions
                var path = resolvedUri.AbsolutePath.ToLowerInvariant();
                if (_config.BlockedExtensions.Any(ext => path.EndsWith(ext)))
                    continue;

                // Skip blocked domains
                var host = resolvedUri.Host.ToLowerInvariant();
                if (_config.BlockedDomains.Any(d =>
                        host == d || host.EndsWith("." + d)))
                    continue;

                // Skip same-page anchors and already-seen URLs
                var normalized = NormalizeUrl(url);
                if (seen.Contains(normalized))
                    continue;

                // Skip links with very short anchor text (likely icons/buttons)
                var anchorText = anchor.TextContent.Trim();
                if (anchorText.Length < 3) continue;

                // Get surrounding paragraph/sentence for richer semantic context
                var (paragraphContext, paragraphLength) = GetSurroundingContext(anchor, anchorText);

                seen.Add(normalized);
                // PositionRatio set below after we know total count
                candidates.Add(new CandidateLink(
                    resolvedUri.AbsoluteUri,
                    anchorText,
                    paragraphContext,
                    0f,
                    paragraphLength));
            }
        }
        catch
        {
            // Parsing failure - return empty
        }

        // Assign position ratios (0.0 = first link = most salient in inverted pyramid)
        if (candidates.Count > 1)
            for (var i = 0; i < candidates.Count; i++)
                candidates[i] = candidates[i] with { PositionRatio = (float)i / (candidates.Count - 1) };

        return candidates;
    }

    /// <summary>
    ///     Extract the surrounding paragraph or sentence context for a link.
    ///     This gives the embedder richer signal than just the anchor text alone.
    ///     E.g. "Researchers at MIT developed a new [quantum framework] that..."
    ///     is more informative than just "quantum framework".
    /// </summary>
    /// <returns>Tuple of (context text for embedding, full paragraph length for salience scoring).</returns>
    private static (string context, int paragraphLength) GetSurroundingContext(IElement anchor, string anchorText)
    {
        // Walk up to find the nearest block-level parent (p, li, div, td, blockquote)
        var parent = anchor.ParentElement;
        while (parent != null)
        {
            var tag = parent.TagName.ToLowerInvariant();
            if (tag is "p" or "li" or "td" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                break;
            // Stop at content boundaries
            if (tag is "article" or "main" or "section" or "body")
            {
                parent = null;
                break;
            }

            parent = parent.ParentElement;
        }

        if (parent == null)
            return (anchorText, anchorText.Length);

        var paragraphText = parent.TextContent.Trim();
        var fullLength = paragraphText.Length;

        // Truncate very long paragraphs — 200 chars is plenty for MiniLM context
        if (paragraphText.Length > 200)
        {
            // Center the context around the anchor text position
            var anchorPos = paragraphText.IndexOf(anchorText, StringComparison.Ordinal);
            if (anchorPos >= 0)
            {
                var start = Math.Max(0, anchorPos - 80);
                var end = Math.Min(paragraphText.Length, anchorPos + anchorText.Length + 80);
                paragraphText = paragraphText[start..end];
            }
            else
            {
                paragraphText = paragraphText[..200];
            }
        }

        return (paragraphText, fullLength);
    }

    /// <summary>
    ///     Fetch a linked page and extract its content.
    /// </summary>
    private async Task<LinkedPage?> FetchLinkedPageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

            var extracted = await _contentExtractor.ExtractAsync(url, cts.Token);
            if (extracted == null || !extracted.IsReadable) return null;

            // Use structured Markdown content when available
            var content = extracted.BestContent;
            if (content.Length > _config.MaxContentLength)
                content = content[.._config.MaxContentLength] + "...";

            // Skip very short content (likely error pages or redirects)
            if (content.Length < 100) return null;

            return new LinkedPage
            {
                Url = url,
                Title = extracted.Title,
                Content = content,
                SiteName = extracted.SiteName
            };
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeUrl(string url)
    {
        return url.Split('?')[0].Split('#')[0].TrimEnd('/').ToLowerInvariant();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }
}

/// <summary>
///     A candidate link extracted from article content, with anchor text,
///     surrounding paragraph context, and positional salience for ranking.
/// </summary>
/// <param name="Url">Resolved absolute URL of the link.</param>
/// <param name="AnchorText">Visible text of the anchor element.</param>
/// <param name="Context">Surrounding paragraph text (up to ~200 chars) for embedding.</param>
/// <param name="PositionRatio">0.0 = first link in content, 1.0 = last. Inverted pyramid: lower = more salient.</param>
/// <param name="ParagraphLength">Length of the parent paragraph. Longer paragraphs = more substantive.</param>
internal record CandidateLink(string Url, string AnchorText, string Context, float PositionRatio, int ParagraphLength);
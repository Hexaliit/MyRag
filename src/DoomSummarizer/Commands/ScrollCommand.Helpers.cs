using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
///     Helper utilities: vibe logic, URL handling, domain filtering, source weights,
///     markdown stripping, in-corpus PageRank, FTS5 backfill.
/// </summary>
public partial class ScrollCommand
{
    /// <summary>
    ///     Prepend vibe-appropriate qualifiers to a search query
    ///     so search results better match the desired sentiment.
    /// </summary>
    internal static readonly HashSet<string> PredefinedVibes =
        new(["doom", "hopeful", "snarky", "funny", "upbeat", "friendly", "toon", "neutral"],
            StringComparer.OrdinalIgnoreCase);

    // ─── Search API rotation ───

    /// <summary>
    ///     Session-level round-robin counter for distributing search queries across
    ///     available API services. Avoids hammering a single provider.
    /// </summary>
    private static int _searchApiRotation;

    /// <summary>
    ///     Returns true if the vibe is custom arbitrary text rather than a predefined vibe.
    /// </summary>
    internal static bool IsCustomVibe(string vibe)
    {
        return !PredefinedVibes.Contains(vibe);
    }

    /// <summary>
    ///     Qualify a search query using a resolved <see cref="Models.Vibe" />.
    ///     Uses the vibe's SearchQualifier when available, falling back to the name-based lookup.
    /// </summary>
    internal static string QualifySearchQuery(string query, Vibe resolvedVibe)
    {
        if (!string.IsNullOrEmpty(resolvedVibe.SearchQualifier))
            return $"{resolvedVibe.SearchQualifier} {query}";
        return QualifySearchQuery(query, resolvedVibe.Name);
    }

    /// <summary>
    ///     Get representative text from a resolved <see cref="Models.Vibe" />.
    ///     Uses the vibe's RepresentativeText when available, falling back to the name-based lookup.
    /// </summary>
    internal static string GetVibeRepresentativeText(Vibe resolvedVibe)
    {
        return resolvedVibe.RepresentativeText ?? GetVibeRepresentativeText(resolvedVibe.Name);
    }

    internal static string QualifySearchQuery(string query, string vibe)
    {
        var qualifier = vibe.ToLowerInvariant() switch
        {
            "doom" => "concerning problems risks issues in",
            "hopeful" => "positive breakthrough innovative upbeat news about",
            "snarky" => "controversial debate criticism of",
            "funny" => "amusing funny quirky weird news about",
            "upbeat" => "exciting breakthrough innovative new developments in",
            "friendly" => "interesting accessible developments in",
            "toon" => "dramatic wild surprising outrageous news about",
            "neutral" => "",
            _ => $"{vibe} articles stories about"
        };

        return string.IsNullOrEmpty(qualifier) ? query : $"{qualifier} {query}";
    }

    /// <summary>
    ///     Compute max cosine similarity between an item embedding and multiple query embeddings.
    ///     For composite queries, items are scored against the BEST matching subquery.
    ///     Falls back to single-query similarity when no subquery embeddings provided.
    /// </summary>
    internal static float ComputeMaxQuerySimilarity(
        float[]? itemEmbedding,
        float[]? primaryQueryEmbedding,
        List<float[]>? subqueryEmbeddings)
    {
        if (itemEmbedding == null) return 0f;

        // If we have subquery embeddings, use max similarity across all of them
        if (subqueryEmbeddings?.Count > 0)
        {
            var maxSim = 0f;
            foreach (var sqEmbed in subqueryEmbeddings)
            {
                var sim = VectorMath.CosineSimilarity(itemEmbedding, sqEmbed);
                if (sim > maxSim) maxSim = sim;
            }

            return maxSim;
        }

        // Otherwise use the primary query embedding
        if (primaryQueryEmbedding != null)
            return VectorMath.CosineSimilarity(itemEmbedding, primaryQueryEmbedding);

        return 0f;
    }

    /// <summary>
    ///     Get representative text for a vibe to use as an embedding target
    ///     for cosine-similarity-based sentiment scoring.
    /// </summary>
    internal static string GetVibeRepresentativeText(string vibe)
    {
        return vibe.ToLowerInvariant() switch
        {
            "doom" =>
                "security vulnerability breach layoffs downturn recession failure risk crisis warning concerning problem threat",
            "hopeful" =>
                "innovation breakthrough positive growth opportunity success launch improvement exciting new achievement progress",
            "snarky" => "hype overrated controversy debate criticism reality check failure ironic absurd",
            "funny" => "amusing hilarious quirky bizarre unexpected weird funny comedy absurd entertaining",
            "upbeat" => "exciting amazing breakthrough launch success win achievement celebration progress incredible",
            "friendly" => "helpful accessible useful interesting development community collaboration knowledge",
            "toon" => "dramatic explosive wild surprising outrageous spectacular unbelievable crisis triumph adventure",
            "neutral" => "technology software engineering development news",
            _ => vibe
        };
    }

    private static string GetSourceFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try
        {
            var host = new Uri(url).Host.Replace("www.", "");
            return host.Split('.')[0];
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    ///     Detect if a URL is likely a homepage or section page (not a specific article).
    ///     Short paths like tmz.com, tmz.com/, tmz.com/celebrity-news are homepages.
    ///     Longer paths like tmz.com/2026/02/09/halle-berry-engaged are articles.
    /// </summary>
    private static bool IsHomepageUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/');
            // No path → definitely homepage (e.g., tmz.com)
            if (string.IsNullOrEmpty(path)) return true;
            // Single segment path → section page (e.g., tmz.com/celebrity-news)
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 1 && !path.Contains('.'))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Filter items by allowed/blocked domain lists.
    ///     AllowedDomains = allowlist (if non-empty, only these pass).
    ///     BlockedDomains = blocklist (removed after allow check).
    /// </summary>
    internal static List<ContentItem> ApplySourceDomainFilter(
        List<ContentItem> items, SourceFilterConfig filter)
    {
        var result = new List<ContentItem>(items.Count);

        foreach (var item in items)
        {
            var host = GetHostFromUrl(item.Url);
            if (string.IsNullOrEmpty(host))
            {
                result.Add(item); // Keep items without URLs (e.g., local content)
                continue;
            }

            // Allowlist: if configured, item must match
            if (filter.AllowedDomains.Count > 0)
                if (!filter.AllowedDomains.Any(d =>
                        host.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                    continue;

            // Blocklist: remove matching items
            if (filter.BlockedDomains.Any(d =>
                    host.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    ///     Apply source reliability weights as RRF score multipliers.
    ///     Matches by source name first, then by URL domain.
    ///     Returns count of items that had weights applied.
    /// </summary>
    internal static int ApplySourceWeights(
        List<ContentItem> items, SourceFilterConfig filter)
    {
        var count = 0;

        foreach (var item in items)
        {
            var weight = 1.0;

            // Match by source name first (hn, reddit, bbc, gnews, search, etc.)
            if (filter.Weights.TryGetValue(item.Source, out var sourceWeight))
            {
                weight = sourceWeight;
            }
            else
            {
                // Fall back to domain matching
                var host = GetHostFromUrl(item.Url);
                if (!string.IsNullOrEmpty(host))
                    foreach (var (pattern, w) in filter.Weights)
                        if (host.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            weight = w;
                            break;
                        }
            }

            if (Math.Abs(weight - 1.0) > 0.001)
            {
                item.RelevanceScore = Math.Min(1.0, item.RelevanceScore * weight);
                count++;
            }
        }

        return count;
    }

    private static string? GetHostFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            return new Uri(url).Host.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Strip markdown formatting for clean LLM consumption.
    ///     Removes headers, link syntax, emphasis, code fences — keeps plain text facts.
    /// </summary>
    internal static string StripMarkdownForLlm(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Remove markdown headers (## Header)
        text = MarkdownHeaderRegex().Replace(text, "");
        // Remove markdown images ![alt](url) — complete or truncated
        text = MarkdownImageRegex().Replace(text, "$1");
        // Remove truncated image at end: ![text](url-without-close...
        text = MarkdownImageTruncatedRegex().Replace(text, "$1");
        // Remove bare ![ at start if no matching ]
        text = MarkdownBareImageRegex().Replace(text, "");
        // Convert [text](url) links to just "text" — complete or truncated
        text = StripMarkdownLinkRegex().Replace(text, "$1");
        text = MarkdownLinkTruncatedRegex().Replace(text, "$1");
        // Remove emphasis markers (*text*, **text**, _text_, __text__)
        text = MarkdownEmphasisRegex().Replace(text, "$2");
        // Remove escaped special chars (\( \) \[ \] etc.)
        text = MarkdownEscapedCharsRegex().Replace(text, "$1");
        // Remove code fences and inline code
        text = text.Replace("```", "").Replace("~~~", "");
        text = MarkdownInlineCodeRegex().Replace(text, "$1");
        // Remove list markers
        text = MarkdownUnorderedListRegex().Replace(text, "");
        text = MarkdownOrderedListRegex().Replace(text, "");
        // Collapse whitespace
        text = CollapseWhitespaceRegex().Replace(text, " ").Trim();

        return text;
    }

    private static Dictionary<string, int> ComputeInCorpusLinkAuthority(List<ContentItem> items)
    {
        // Build set of all article URLs in corpus (normalized)
        var corpusUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            if (!string.IsNullOrEmpty(item.Url))
                corpusUrls.Add(NormalizeUrlForAuthority(item.Url));

        // Count incoming links from LinkedPages
        var inLinkCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        foreach (var linked in item.LinkedPages)
        {
            var normalizedLinked = NormalizeUrlForAuthority(linked.Url);
            if (corpusUrls.Contains(normalizedLinked))
            {
                inLinkCounts.TryGetValue(normalizedLinked, out var count);
                inLinkCounts[normalizedLinked] = count + 1;
            }
        }

        return inLinkCounts;
    }

    private static string NormalizeUrlForAuthority(string url)
    {
        return url.Split('?')[0].TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    ///     Backfill the FTS5 index and keyword corpus for existing KB items
    ///     that were stored before FTS5 was introduced. Runs automatically
    ///     on first query when the FTS5 table is empty.
    /// </summary>
    private static async Task BackfillFtsIndexAsync(StorageService storage, bool quiet)
    {
        var allItems = await storage.GetAllItemsAsync();
        if (allItems.Count == 0) return;

        if (!quiet)
            AnsiConsole.MarkupLine($"[grey]Backfilling FTS5 index for {allItems.Count} existing items...[/]");

        var count = 0;
        foreach (var stored in allItems)
        {
            var profile = DocumentProfileService.ExtractProfile(stored.Title, stored.Content ?? "");
            var contentPreview = (stored.Content ?? "").Length > 2000
                ? stored.Content![..2000]
                : stored.Content ?? "";
            await storage.IndexDocumentFtsAsync(stored.Id, stored.Title, profile.KeywordsText, contentPreview);
            await storage.UpdateKeywordCorpusAsync(profile.TopKeywords.Select(k => k.Keyword));
            count++;
        }

        if (!quiet)
            AnsiConsole.MarkupLine($"[green]FTS5 backfill complete: {count} items indexed[/]");
    }

    // --- Generated Regex patterns for StripMarkdownForLlm ---

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeaderRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)?")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*$")]
    private static partial Regex MarkdownImageTruncatedRegex();

    [GeneratedRegex(@"^!\[")]
    private static partial Regex MarkdownBareImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)?")]
    private static partial Regex StripMarkdownLinkRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*$")]
    private static partial Regex MarkdownLinkTruncatedRegex();

    [GeneratedRegex(@"(\*{1,2}|_{1,2})(.+?)\1")]
    private static partial Regex MarkdownEmphasisRegex();

    [GeneratedRegex(@"\\([()[\]])")]
    private static partial Regex MarkdownEscapedCharsRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex MarkdownInlineCodeRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownUnorderedListRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownOrderedListRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();

    /// <summary>
    ///     Build a list of available search services in priority order, then rotate
    ///     the starting position so each call uses a different primary service.
    ///     Returns an async function that tries services in rotated order.
    /// </summary>
    internal static Func<CancellationToken, Task<List<ContentItem>>> BuildRotatedSearchTask(
        string qualifiedQuery, int searchLimit,
        HttpClient httpClient, ApiKeyService apiKeys, ApiBudgetService apiBudget,
        CircuitBreakerService circuitBreaker)
    {
        // Collect all available search services (order matters for fallback)
        var available = new List<(string name, Func<Task<List<ContentItem>>> search)>();

        if (apiKeys.HasGoogleSearch)
            available.Add(("google_search", () =>
                new GoogleSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                    .SearchAsync(qualifiedQuery, searchLimit)));
        if (apiKeys.IsAvailable("brave_search"))
            available.Add(("brave_search", () => new BraveSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                .SearchAsync(qualifiedQuery, searchLimit)));
        if (apiKeys.IsAvailable("serper"))
            available.Add(("serper", () => new SerperSearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                .SearchAsync(qualifiedQuery, searchLimit)));
        if (apiKeys.IsAvailable("tavily"))
            available.Add(("tavily", () => new TavilySearchService(httpClient, apiKeys, apiBudget, circuitBreaker)
                .SearchAsync(qualifiedQuery, searchLimit)));

        // DDG always available as last resort (no API key needed)
        available.Add(("duckduckgo", () => new DuckDuckGoSearch(httpClient, circuitBreaker)
            .SearchAsync(qualifiedQuery, searchLimit)));

        return async _ =>
        {
            if (available.Count <= 1)
                return await available[0].search();

            // Rotate: pick starting index, try each in order, return first success
            var startIdx =
                Interlocked.Increment(ref _searchApiRotation) % (available.Count - 1); // exclude DDG from rotation
            var paidServices = available.Count - 1; // DDG is always last

            for (var i = 0; i < paidServices; i++)
            {
                var idx = (startIdx + i) % paidServices;
                var (name, search) = available[idx];

                // Skip if circuit is open for this service
                if (ApiRateLimiter.IsCircuitOpen(name))
                    continue;

                try
                {
                    var results = await search();
                    if (results.Count > 0)
                        return results;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Search service failed: {ex.Message}");
                }
            }

            // All paid services failed/empty — fall back to DDG
            return await available[^1].search();
        };
    }

    // ─── Selected sources indicator ───

    /// <summary>
    ///     Render a compact one-line display of the sources the sentinel selected,
    ///     color-coded by availability: green = active, red = circuit open, grey = no API key.
    ///     Shown after sentinel interpretation in both default and --full modes.
    /// </summary>
    internal static void RenderSelectedSources(
        List<string> selectedSources,
        ApiKeyService apiKeys,
        CircuitBreakerService circuitBreaker)
    {
        if (selectedSources.Count == 0) return;

        var parts = new List<string>();
        foreach (var source in selectedSources)
        {
            var root = source.Split(':')[0];
            var display = Markup.Escape(source);

            // Map CLI source root to API service name for availability check
            var apiServiceName = root.ToLowerInvariant() switch
            {
                "gnews" or "gnews_topic" => "google_search",
                "search" => null, // uses rotated search (Brave/Serper/Tavily/DDG)
                "hn" or "reddit" or "lobsters" or "devto" or "bbc" or "guardian"
                    or "cnn" or "reuters" or "npr" or "ars" or "verge"
                    or "techcrunch" or "wired" or "theregister" or "theonion"
                    or "babylonbee" or "spaceflight" or "earthquake"
                    or "sciencedaily" or "phys" or "carbonbrief"
                    or "factcheck" or "wikipedia" or "arxiv"
                    => null, // RSS/scrape sources — always available
                "newsapi" => "newsapi",
                "newsdata" => "newsdata",
                "currents" => "currents",
                _ => root
            };

            string color;
            if (apiServiceName == null)
            {
                // RSS/scrape/search rotation — check circuit breaker by root name
                var circuitOpen = circuitBreaker.IsCircuitOpen(root);
                color = circuitOpen ? "red" : "green";
            }
            else
            {
                if (!apiKeys.IsAvailable(apiServiceName))
                    color = "grey";
                else if (circuitBreaker.IsCircuitOpen(apiServiceName))
                    color = "red";
                else
                    color = "green";
            }

            parts.Add($"[{color}]{display}[/]");
        }

        AnsiConsole.MarkupLine($"[bold grey]Sources[/]  {string.Join("  ", parts)}");
    }

    // ─── Startup info panel ───

    /// <summary>
    ///     Render a compact startup info panel showing config, LLM, embedding, search APIs,
    ///     and KB stats. Uses full terminal width adaptively.
    /// </summary>
    internal static void RenderStartupPanel(
        DoomConfig config,
        string? configPath,
        LlmRouter llmRouter,
        IEmbeddingService embedding,
        ApiKeyService apiKeys,
        CircuitBreakerService circuitBreaker,
        string? prompt)
    {
        var width = Math.Min(AnsiConsole.Profile.Width, 120);

        // Build search service status
        var searchServices = new[]
        {
            "google_search", "brave_search", "serper", "tavily", "newsapi", "newsdata", "currents", "jina", "duckduckgo"
        };
        var searchStatus = new List<string>();
        foreach (var svc in searchServices)
        {
            var shortName = svc switch
            {
                "google_search" => "Google",
                "brave_search" => "Brave",
                "serper" => "Serper",
                "tavily" => "Tavily",
                "newsapi" => "NewsAPI",
                "newsdata" => "NewsData",
                "currents" => "Currents",
                "jina" => "Jina",
                "duckduckgo" => "DDG",
                _ => svc
            };

            if (svc == "duckduckgo")
            {
                // DDG is always available (no key needed)
                var ddgCircuit = circuitBreaker.IsCircuitOpen("duckduckgo");
                searchStatus.Add(ddgCircuit ? $"[red]{shortName}[/]" : $"[green]{shortName}[/]");
                continue;
            }

            if (!apiKeys.IsAvailable(svc))
            {
                searchStatus.Add($"[grey]{shortName}[/]");
                continue;
            }

            var isOpen = circuitBreaker.IsCircuitOpen(svc);
            searchStatus.Add(isOpen ? $"[red]{shortName}[/]" : $"[green]{shortName}[/]");
        }

        // Embedding info from IEmbeddingService
        var embeddingInfo = $"[green]ONNX ({embedding.EmbeddingDimension}d)[/]";

        // Build the grid
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
        grid.AddColumn(new GridColumn());

        grid.AddRow("[bold grey]Config[/]", Markup.Escape(configPath ?? "embedded default"));
        grid.AddRow("[bold grey]LLM[/]", Markup.Escape(llmRouter.StatusDescription));
        grid.AddRow("[bold grey]Embed[/]", embeddingInfo);
        grid.AddRow("[bold grey]Search[/]", string.Join(" ", searchStatus));

        if (!string.IsNullOrEmpty(prompt))
            grid.AddRow("[bold grey]Query[/]",
                Markup.Escape(prompt.Length > width - 20 ? prompt[..(width - 23)] + "..." : prompt));

        var panel = new Panel(grid)
            .Header("[bold cyan]DoomSummarizer[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand();

        // Constrain to terminal width
        if (width < 120)
            panel.Width = width;

        AnsiConsole.Write(panel);
    }
}
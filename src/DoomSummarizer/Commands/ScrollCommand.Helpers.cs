using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
/// Helper utilities: vibe logic, URL handling, domain filtering, source weights,
/// markdown stripping, in-corpus PageRank, FTS5 backfill.
/// </summary>
public partial class ScrollCommand
{
    /// <summary>
    /// Prepend vibe-appropriate qualifiers to a search query
    /// so search results better match the desired sentiment.
    /// </summary>
    internal static readonly HashSet<string> PredefinedVibes =
        new(["doom", "hopeful", "snarky", "funny", "upbeat", "friendly", "toon", "neutral"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the vibe is custom arbitrary text rather than a predefined vibe.
    /// </summary>
    internal static bool IsCustomVibe(string vibe) =>
        !PredefinedVibes.Contains(vibe);

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
    /// Compute max cosine similarity between an item embedding and multiple query embeddings.
    /// For composite queries, items are scored against the BEST matching subquery.
    /// Falls back to single-query similarity when no subquery embeddings provided.
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
                var sim = EmbeddingService.CosineSimilarity(itemEmbedding, sqEmbed);
                if (sim > maxSim) maxSim = sim;
            }
            return maxSim;
        }

        // Otherwise use the primary query embedding
        if (primaryQueryEmbedding != null)
            return EmbeddingService.CosineSimilarity(itemEmbedding, primaryQueryEmbedding);

        return 0f;
    }

    /// <summary>
    /// Get representative text for a vibe to use as an embedding target
    /// for cosine-similarity-based sentiment scoring.
    /// </summary>
    internal static string GetVibeRepresentativeText(string vibe)
    {
        return vibe.ToLowerInvariant() switch
        {
            "doom" => "security vulnerability breach layoffs downturn recession failure risk crisis warning concerning problem threat",
            "hopeful" => "innovation breakthrough positive growth opportunity success launch improvement exciting new achievement progress",
            "snarky" => "hype overrated controversy debate criticism reality check failure ironic absurd",
            "funny" => "amusing hilarious quirky bizarre unexpected weird funny comedy absurd entertaining",
            "upbeat" => "exciting amazing breakthrough launch success win achievement celebration progress incredible",
            "friendly" => "helpful accessible useful interesting development community collaboration knowledge",
            "toon" => "dramatic explosive wild surprising outrageous spectacular unbelievable crisis triumph adventure",
            "neutral" => "technology software engineering development news",
            _ => vibe
        };
    }

    /// <summary>
    /// Infer a basic topic from the source name when embeddings aren't available.
    /// </summary>
    internal static string InferTopicFromSource(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "hn" => "technology",
            "reddit" => "technology",
            "bbc" or "guardian" or "cnn" or "reuters" => "world",
            "gnews" => "general",
            "so" => "technology",
            "ars" or "verge" => "technology",
            _ => "general"
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
        catch { return "?"; }
    }

    /// <summary>
    /// Filter items by allowed/blocked domain lists.
    /// AllowedDomains = allowlist (if non-empty, only these pass).
    /// BlockedDomains = blocklist (removed after allow check).
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
            {
                if (!filter.AllowedDomains.Any(d =>
                    host.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            // Blocklist: remove matching items
            if (filter.BlockedDomains.Any(d =>
                host.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// Apply source reliability weights as RRF score multipliers.
    /// Matches by source name first, then by URL domain.
    /// Returns count of items that had weights applied.
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
                {
                    foreach (var (pattern, w) in filter.Weights)
                    {
                        if (host.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            weight = w;
                            break;
                        }
                    }
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
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return null; }
    }

    /// <summary>
    /// Strip markdown formatting for clean LLM consumption.
    /// Removes headers, link syntax, emphasis, code fences — keeps plain text facts.
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
        {
            if (!string.IsNullOrEmpty(item.Url))
                corpusUrls.Add(NormalizeUrlForAuthority(item.Url));
        }

        // Count incoming links from LinkedPages
        var inLinkCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var linked in item.LinkedPages)
            {
                var normalizedLinked = NormalizeUrlForAuthority(linked.Url);
                if (corpusUrls.Contains(normalizedLinked))
                {
                    inLinkCounts.TryGetValue(normalizedLinked, out var count);
                    inLinkCounts[normalizedLinked] = count + 1;
                }
            }
        }

        return inLinkCounts;
    }

    private static string NormalizeUrlForAuthority(string url) =>
        url.Split('?')[0].TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// Backfill the FTS5 index and keyword corpus for existing KB items
    /// that were stored before FTS5 was introduced. Runs automatically
    /// on first query when the FTS5 table is empty.
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
}

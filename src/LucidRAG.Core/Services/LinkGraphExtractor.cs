using DoomSummarizer.Services;

namespace LucidRAG.Services;

/// <summary>
///     Extracts URLs, DOIs, and arXiv IDs from any document text for graph edge creation.
///     Every document gets link extraction — this is core behavior, not domain-specific.
/// </summary>
public static class LinkGraphExtractor
{
    // Domains to skip (not useful as graph edges)
    private static readonly HashSet<string> SkipDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "example.com", "example.org",
        "schema.org", "www.w3.org", "xmlns.com"
    };

    public static List<ExtractedLink> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new Dictionary<string, ExtractedLink>(StringComparer.OrdinalIgnoreCase);

        // Extract DOIs first (more specific than URLs)
        foreach (System.Text.RegularExpressions.Match match in AcademicPatterns.DoiRegex().Matches(text))
        {
            var raw = match.Value;
            var doi = AcademicPatterns.NormalizeDoi(raw);
            if (string.IsNullOrEmpty(doi)) continue;

            var key = $"doi:{doi}";
            if (!results.ContainsKey(key))
            {
                var context = GetSurroundingContext(text, match.Index, 100);
                results[key] = new ExtractedLink(doi, LinkType.Doi, context);
            }
        }

        // Extract arXiv IDs
        foreach (System.Text.RegularExpressions.Match match in AcademicPatterns.ArxivCombinedRegex().Matches(text))
        {
            var arxivId = match.Groups[1].Value;
            var key = $"arxiv:{arxivId}";
            if (!results.ContainsKey(key))
            {
                var context = GetSurroundingContext(text, match.Index, 100);
                results[key] = new ExtractedLink(arxivId, LinkType.ArxivId, context);
            }
        }

        // Extract URLs (skip those already captured as DOI/arXiv)
        foreach (System.Text.RegularExpressions.Match match in AcademicPatterns.UrlRegex().Matches(text))
        {
            var url = CleanUrl(match.Value);
            if (string.IsNullOrEmpty(url)) continue;
            if (IsSkippableUrl(url)) continue;

            // Skip if this URL is a DOI link already captured
            if (url.Contains("doi.org/10.")) continue;
            // Skip if this URL is an arXiv link already captured
            if (url.Contains("arxiv.org/")) continue;

            var key = $"url:{url}";
            if (!results.ContainsKey(key))
            {
                var context = GetSurroundingContext(text, match.Index, 100);
                results[key] = new ExtractedLink(url, LinkType.Url, context);
            }
        }

        return results.Values.ToList();
    }

    private static string CleanUrl(string url)
    {
        // Strip trailing punctuation that's not part of URL
        return url.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
    }

    private static bool IsSkippableUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return SkipDomains.Contains(uri.Host) ||
                   SkipDomains.Contains(uri.Host.Replace("www.", ""));
        }
        catch
        {
            return true;
        }
    }

    private static string GetSurroundingContext(string text, int offset, int radius)
    {
        var start = Math.Max(0, offset - radius);
        var end = Math.Min(text.Length, offset + radius);
        return text[start..end].Trim();
    }
}

/// <summary>
///     A link extracted from document text.
/// </summary>
/// <param name="Value">The URL, DOI, or arXiv ID</param>
/// <param name="Type">The type of link</param>
/// <param name="Context">Surrounding text for context</param>
public record ExtractedLink(string Value, LinkType Type, string Context);

public enum LinkType
{
    Url,
    Doi,
    ArxivId
}

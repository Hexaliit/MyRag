using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DoomSummarizer.Services;

/// <summary>
///     Shared regex patterns, constants, and utility methods for academic citation extraction.
///     Consolidates DOI/arXiv patterns previously duplicated across ResearchPlugin, CitationResolver,
///     ArxivFetcher, LinkGraphExtractor, and FollowPapersCommand.
/// </summary>
public static partial class AcademicPatterns
{
    // ── Constants ──────────────────────────────────────────────────────

    /// <summary>Atom XML namespace used by the arXiv API.</summary>
    public static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>arXiv-specific XML namespace for extended metadata.</summary>
    public static readonly XNamespace ArxivXmlNamespace = "http://arxiv.org/schemas/atom";

    /// <summary>Base URL for the arXiv query API.</summary>
    public const string ArxivApiBaseUrl = "http://export.arxiv.org/api/query";

    /// <summary>Base URL for ar5iv HTML paper rendering.</summary>
    public const string Ar5ivBaseUrl = "https://ar5iv.labs.arxiv.org/html/";

    /// <summary>Base URL for the CrossRef works API.</summary>
    public const string CrossRefApiBaseUrl = "https://api.crossref.org/works/";

    // ── Regex Patterns ─────────────────────────────────────────────────

    /// <summary>
    ///     Matches DOIs in text, with optional doi.org URL or "doi:" prefix.
    ///     Example matches: "10.1234/foo", "https://doi.org/10.1234/foo", "doi: 10.1234/foo"
    /// </summary>
    [GeneratedRegex(@"(?:https?://doi\.org/|doi:\s*)?10\.\d{4,}/[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    public static partial Regex DoiRegex();

    /// <summary>
    ///     Matches arXiv IDs from URLs: arxiv.org/abs/2301.12345 or arxiv.org/pdf/2301.12345v2
    /// </summary>
    [GeneratedRegex(@"arxiv\.org/(?:abs|pdf)/(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase)]
    public static partial Regex ArxivUrlRegex();

    /// <summary>
    ///     Matches arXiv IDs from text references: arXiv:2301.12345 or arXiv: 2301.12345v2
    /// </summary>
    [GeneratedRegex(@"arXiv:\s*(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase)]
    public static partial Regex ArxivTextRegex();

    /// <summary>
    ///     Combined pattern matching both arXiv:XXXX and arxiv.org/abs|pdf/XXXX forms.
    /// </summary>
    [GeneratedRegex(@"(?:arXiv:\s*|https?://arxiv\.org/(?:abs|pdf)/)(\d{4}\.\d{4,5}(?:v\d+)?)",
        RegexOptions.IgnoreCase)]
    public static partial Regex ArxivCombinedRegex();

    /// <summary>
    ///     Matches a bare arXiv ID (no prefix): 2301.12345 or 2301.12345v2
    /// </summary>
    [GeneratedRegex(@"^\d{4}\.\d{4,5}(?:v\d+)?$")]
    public static partial Regex ArxivBareIdRegex();

    /// <summary>
    ///     Matches a bare DOI (no URL prefix): 10.xxxx/yyyy
    /// </summary>
    [GeneratedRegex(@"^10\.\d{4,}/\S+$")]
    public static partial Regex DoiBareIdRegex();

    /// <summary>
    ///     Matches HTTP/HTTPS URLs.
    /// </summary>
    [GeneratedRegex(@"https?://[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    public static partial Regex UrlRegex();

    /// <summary>
    ///     Matches one or more whitespace characters (for collapsing).
    /// </summary>
    [GeneratedRegex(@"\s+")]
    public static partial Regex WhitespaceRegex();

    // ── Utility Methods ────────────────────────────────────────────────

    /// <summary>
    ///     Normalize a raw DOI string by stripping URL prefixes, "doi:" prefix, and trailing punctuation.
    ///     Returns empty string if the result doesn't start with "10.".
    /// </summary>
    public static string NormalizeDoi(string raw)
    {
        var doi = raw;
        if (doi.StartsWith("doi:", StringComparison.OrdinalIgnoreCase))
            doi = doi[4..].TrimStart();
        if (doi.StartsWith("https://doi.org/", StringComparison.OrdinalIgnoreCase))
            doi = doi[16..];
        if (doi.StartsWith("http://doi.org/", StringComparison.OrdinalIgnoreCase))
            doi = doi[15..];

        if (!doi.StartsWith("10.")) return "";

        doi = doi.TrimEnd('.', ',', ';', ')', ']');
        return doi;
    }

    /// <summary>
    ///     Collapse all whitespace runs into a single space and trim.
    /// </summary>
    public static string CleanWhitespace(string text)
    {
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    /// <summary>
    ///     Extract the arXiv ID from a URL like "http://arxiv.org/abs/2401.12345v1".
    ///     Returns null if no match.
    /// </summary>
    public static string? ExtractArxivIdFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var match = ArxivUrlRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    ///     Strip the version suffix from an arXiv ID: "2301.12345v2" → "2301.12345".
    /// </summary>
    public static string StripArxivVersion(string arxivId)
    {
        var vIdx = arxivId.IndexOf('v');
        return vIdx >= 0 ? arxivId[..vIdx] : arxivId;
    }

    /// <summary>
    ///     Extract DOI and arXiv citation IDs from text. Skips arXiv DOI aliases (10.48550/arXiv.XXXX).
    ///     Optionally excludes a "self" arXiv ID to avoid self-citation.
    /// </summary>
    public static List<(string type, string id)> ExtractCitationIds(string text, string? selfArxivId = null)
    {
        var results = new List<(string type, string id)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract DOIs (skip arXiv DOI aliases like 10.48550/arXiv.XXXX)
        foreach (Match match in DoiRegex().Matches(text))
        {
            var doi = NormalizeDoi(match.Value);
            if (string.IsNullOrEmpty(doi)) continue;
            if (doi.Contains("arXiv", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add($"doi:{doi}"))
                results.Add(("doi", doi));
        }

        // Extract arXiv IDs from arXiv: text pattern
        foreach (Match match in ArxivTextRegex().Matches(text))
        {
            var id = match.Groups[1].Value;
            if (id != selfArxivId && seen.Add($"arxiv:{id}"))
                results.Add(("arxiv", id));
        }

        // Extract arXiv IDs from HTML links (arxiv.org/abs/XXXX)
        foreach (Match match in ArxivUrlRegex().Matches(text))
        {
            var id = match.Groups[1].Value;
            if (id != selfArxivId && seen.Add($"arxiv:{id}"))
                results.Add(("arxiv", id));
        }

        return results;
    }

    /// <summary>
    ///     Fetch the full paper text from ar5iv (HTML rendering of arXiv papers).
    ///     Returns plain text with HTML tags stripped, or null on failure.
    /// </summary>
    public static async Task<string?> FetchAr5ivTextAsync(HttpClient httpClient, string arxivId,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"{Ar5ivBaseUrl}{arxivId}";
            var response = await httpClient.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);

            // Strip HTML tags to get plain text (lightweight extraction)
            var text = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            return text.Length > 100 ? text : null;
        }
        catch
        {
            return null;
        }
    }
}

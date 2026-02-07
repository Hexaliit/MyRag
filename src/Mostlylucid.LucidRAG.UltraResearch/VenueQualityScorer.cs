using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace LucidRAG.UltraResearch;

/// <summary>
///     Computes a composite venue quality score (0-1) for academic papers.
///     Combines citation signals, venue type, and publication status.
///     Used as an RRF signal during retrieval to boost papers from reputable sources.
/// </summary>
public static class VenueQualityScorer
{
    /// <summary>
    ///     Well-known venue tiers. Matched by normalized name against S2 venue or CrossRef container-title.
    ///     Overrides the generic venueTypeSignal when matched.
    /// </summary>
    private static readonly Dictionary<string, double> VenueTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Top-tier journals
        ["nature"] = 1.0,
        ["science"] = 1.0,
        ["cell"] = 0.95,
        ["the lancet"] = 0.95,
        ["new england journal of medicine"] = 0.95,
        ["jama"] = 0.90,
        ["bmj"] = 0.90,
        ["proceedings of the national academy of sciences"] = 0.90,
        ["pnas"] = 0.90,
        ["physical review letters"] = 0.90,

        // Top-tier CS conferences
        ["neurips"] = 0.90,
        ["nips"] = 0.90,
        ["icml"] = 0.90,
        ["iclr"] = 0.90,
        ["cvpr"] = 0.85,
        ["acl"] = 0.85,
        ["emnlp"] = 0.85,
        ["naacl"] = 0.80,
        ["aaai"] = 0.85,
        ["ijcai"] = 0.80,
        ["iccv"] = 0.85,
        ["eccv"] = 0.80,
        ["sigir"] = 0.80,
        ["kdd"] = 0.85,
        ["www"] = 0.80,
        ["chi"] = 0.80,

        // Strong journals
        ["nature machine intelligence"] = 0.90,
        ["nature communications"] = 0.85,
        ["scientific reports"] = 0.70,
        ["plos one"] = 0.65,
        ["ieee transactions on pattern analysis and machine intelligence"] = 0.85,
        ["journal of machine learning research"] = 0.85,
        ["jmlr"] = 0.85,
        ["transactions of the association for computational linguistics"] = 0.85,
        ["tacl"] = 0.85,
        ["artificial intelligence"] = 0.80,

        // Preprint servers (low tier)
        ["arxiv"] = 0.30,
        ["biorxiv"] = 0.30,
        ["medrxiv"] = 0.30,
        ["ssrn"] = 0.30,
    };

    /// <summary>
    ///     Compute a composite venue quality score (0-1) from available paper metadata.
    /// </summary>
    /// <param name="s2Paper">Semantic Scholar paper data (may be null).</param>
    /// <param name="crossRefMetadata">CrossRef citation metadata (may be null).</param>
    /// <param name="fallbackVenue">Fallback venue name if not available from S2 or CrossRef.</param>
    /// <returns>A score between 0.0 and 1.0.</returns>
    public static double ComputeVenueQuality(S2Paper? s2Paper, CitationMetadata? crossRefMetadata, string? fallbackVenue = null)
    {
        var citations = s2Paper?.CitationCount ?? 0;
        var influential = s2Paper?.InfluentialCitationCount ?? 0;

        // Citation signal: log-scaled, caps at ~1000 citations
        var citationSignal = citations > 0
            ? Math.Min(1.0, Math.Log(1 + citations) / Math.Log(1 + 1000))
            : 0.0;

        // Influential citation signal: log-scaled, caps at ~100 influential citations
        var influentialCitationSignal = influential > 0
            ? Math.Min(1.0, Math.Log(1 + influential) / Math.Log(1 + 100))
            : 0.0;

        // Resolve venue name from all sources
        var venueName = s2Paper?.PublicationVenue?.Name
                        ?? s2Paper?.Venue
                        ?? crossRefMetadata?.Venue
                        ?? fallbackVenue;

        // Venue type signal from S2 publication venue type
        var venueTypeSignal = GetVenueTypeSignal(s2Paper?.PublicationVenue?.Type);

        // Check if we have a tier override from well-known venues
        if (!string.IsNullOrEmpty(venueName))
        {
            var tierScore = MatchVenueTier(venueName);
            if (tierScore.HasValue)
                venueTypeSignal = tierScore.Value;
        }

        // Publication signal: has DOI + venue = published
        var hasDoi = !string.IsNullOrEmpty(s2Paper?.Doi) || !string.IsNullOrEmpty(crossRefMetadata?.Doi);
        var hasVenue = !string.IsNullOrEmpty(venueName);
        var publicationSignal = hasDoi && hasVenue ? 1.0 : hasDoi ? 0.6 : 0.3;

        // Composite score
        var score = 0.35 * citationSignal
                    + 0.25 * influentialCitationSignal
                    + 0.25 * venueTypeSignal
                    + 0.15 * publicationSignal;

        return Math.Min(1.0, Math.Max(0.0, score));
    }

    /// <summary>
    ///     Get venue type signal from S2 publication venue type string.
    /// </summary>
    internal static double GetVenueTypeSignal(string? venueType)
    {
        if (string.IsNullOrEmpty(venueType))
            return 0.4; // Unknown

        return venueType.ToLowerInvariant() switch
        {
            "journal" => 0.7,
            "conference" => 0.6,
            _ => 0.4 // Unknown type
        };
    }

    /// <summary>
    ///     Try to match a venue name against the tier dictionary with fuzzy matching.
    ///     Normalizes by stripping common prefixes ("Proceedings of the", "Annual", etc.).
    /// </summary>
    internal static double? MatchVenueTier(string venueName)
    {
        var normalized = NormalizeVenueName(venueName);

        // Direct match
        if (VenueTiers.TryGetValue(normalized, out var score))
            return score;

        // Try the original name
        if (VenueTiers.TryGetValue(venueName, out score))
            return score;

        // Check if any tier key is contained in the venue name (for long official names)
        foreach (var (key, value) in VenueTiers)
        {
            if (normalized.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    /// <summary>
    ///     Normalize a venue name by stripping common prefixes and trimming.
    /// </summary>
    internal static string NormalizeVenueName(string name)
    {
        var result = name.Trim();

        // Strip common prefixes
        string[] prefixes =
        [
            "Proceedings of the ",
            "Proceedings of ",
            "Annual Conference on ",
            "Annual Meeting of the ",
            "International Conference on ",
            "Conference on ",
            "The "
        ];

        foreach (var prefix in prefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[prefix.Length..].Trim();
                break; // Only strip one prefix
            }
        }

        return result;
    }
}

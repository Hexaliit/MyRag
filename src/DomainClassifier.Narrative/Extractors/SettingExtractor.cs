using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Narrative.Extractors;

/// <summary>
///     Extracts setting information from narrative text: locations, time periods, and atmosphere.
/// </summary>
public static partial class SettingExtractor
{
    // Location after setting verbs: "arrived at Pemberley", "lived in London"
    [GeneratedRegex(
        @"(?:arrived\s+at|lived\s+in|traveled\s+to|journeyed\s+to|came\s+to|went\s+to|returned\s+to|set\s+out\s+for|sailed\s+to|rode\s+to|walked\s+to|set\s+in|takes\s+place\s+in|located\s+in)\s+(?:the\s+)?([A-Z][a-z]+(?:\s+[A-Z][a-z]+)*)",
        RegexOptions.Compiled)]
    private static partial Regex LocationVerbRegex();

    // Time period: "in the year 1818", "during the 19th century"
    [GeneratedRegex(
        @"(?:in\s+the\s+year\s+|during\s+the\s+|in\s+|around\s+)(\d{3,4}s?|(?:\d{1,2}(?:st|nd|rd|th)\s+century)|(?:medieval|Victorian|Elizabethan|Regency|Georgian|Edwardian)\s+(?:era|period|age|times)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimePeriodRegex();

    // Locations that are too broad to be meaningful narrative settings,
    // or commonly appear in boilerplate/legal text (e.g. Project Gutenberg disclaimers)
    private static readonly HashSet<string> LocationFalsePositives = new(StringComparer.OrdinalIgnoreCase)
    {
        "United States", "United Kingdom", "European Union",
        "North America", "South America", "Project Gutenberg"
    };

    // Atmosphere/time-of-day words
    private static readonly HashSet<string> AtmosphereTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "dawn", "dusk", "twilight", "midnight", "moonlight", "starlight",
        "fog", "mist", "storm", "thunder", "lightning", "rain",
        "snow", "frost", "wind", "darkness", "shadow", "gloom",
        "sunshine", "warmth", "firelight", "candlelight"
    };

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();

        // Locations
        foreach (Match match in LocationVerbRegex().Matches(text))
        {
            var location = match.Groups[1].Value.Trim();
            if (location.Length >= 2 && !LocationFalsePositives.Contains(location))
            {
                entities.Add(new DomainEntity(
                    Text: location,
                    EntityType: "setting_location",
                    DomainId: "narrative",
                    StartOffset: match.Groups[1].Index,
                    EndOffset: match.Groups[1].Index + location.Length,
                    Confidence: 0.85));
            }
        }

        // Time periods
        foreach (Match match in TimePeriodRegex().Matches(text))
        {
            var period = match.Groups[1].Value.Trim();
            entities.Add(new DomainEntity(
                Text: period,
                EntityType: "time_period",
                DomainId: "narrative",
                StartOffset: match.Groups[1].Index,
                EndOffset: match.Groups[1].Index + period.Length,
                Confidence: 0.80));
        }

        // Atmosphere terms (lower confidence, context-dependent) — find all occurrences
        var textLower = text.ToLowerInvariant();
        foreach (var term in AtmosphereTerms)
        {
            var idx = 0;
            while ((idx = textLower.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
            {
                entities.Add(new DomainEntity(
                    Text: term,
                    EntityType: "atmosphere",
                    DomainId: "narrative",
                    StartOffset: idx,
                    EndOffset: idx + term.Length,
                    Confidence: 0.60));
                idx += term.Length;
            }
        }

        return entities;
    }
}

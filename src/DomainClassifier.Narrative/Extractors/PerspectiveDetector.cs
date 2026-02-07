using System.Text.RegularExpressions;

namespace DomainClassifier.Narrative.Extractors;

/// <summary>
///     Detects narrative perspective/point of view using pronoun density analysis.
/// </summary>
public static partial class PerspectiveDetector
{
    // First-person markers (lowered — text is compared via ToLowerInvariant)
    private static readonly string[] FirstPersonMarkers = [" i ", " my ", " me ", " myself ", " mine "];

    // Third-person markers
    private static readonly string[] ThirdPersonMarkers =
        [" he ", " she ", " his ", " her ", " him ", " himself ", " herself "];

    // Epistolary markers
    [GeneratedRegex(@"(?m)^(?:Dear\s+|My\s+dear\s+|Dearest\s+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex EpistolaryRegex();

    // Date header (letter format)
    [GeneratedRegex(@"(?m)^\w+\s+\d{1,2},?\s+\d{4}", RegexOptions.Multiline)]
    private static partial Regex DateHeaderRegex();

    /// <summary>
    ///     Detect the narrative perspective of the text.
    /// </summary>
    public static (string Perspective, double Confidence) Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("unknown", 0.0);

        var paddedText = $" {text} "; // Pad for word boundary matching
        var textLower = paddedText.ToLowerInvariant();
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount == 0) return ("unknown", 0.0);

        // Count pronoun occurrences
        var firstPersonCount = 0;
        foreach (var marker in FirstPersonMarkers)
        {
            var idx = 0;
            while ((idx = textLower.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
            {
                firstPersonCount++;
                idx += marker.Length;
            }
        }

        var thirdPersonCount = 0;
        foreach (var marker in ThirdPersonMarkers)
        {
            var idx = 0;
            while ((idx = textLower.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
            {
                thirdPersonCount++;
                idx += marker.Length;
            }
        }

        var firstPersonDensity = (double)firstPersonCount / wordCount;
        var thirdPersonDensity = (double)thirdPersonCount / wordCount;

        // Check for epistolary (letters)
        var dearMatches = EpistolaryRegex().Matches(text).Count;
        var dateMatches = DateHeaderRegex().Matches(text).Count;
        if (dearMatches >= 2 || (dearMatches >= 1 && dateMatches >= 1))
        {
            var epistolaryConfidence = Math.Min(1.0, (dearMatches + dateMatches) * 0.25);
            return ("epistolary", Math.Round(Math.Max(0.6, epistolaryConfidence), 4));
        }

        // First-person: "I" density > 3% of words and dominates over third-person
        if (firstPersonDensity > 0.03 && firstPersonDensity > thirdPersonDensity)
        {
            var confidence = Math.Min(1.0, firstPersonDensity * 10);
            return ("first-person", Math.Round(Math.Max(0.6, confidence), 4));
        }

        // Third-person dominates
        if (thirdPersonDensity > 0.02 && thirdPersonDensity > firstPersonDensity)
        {
            // Distinguish third-person limited from omniscient
            // Omniscient: multiple he/she subjects with internal thought markers
            var hasMultipleSubjects = textLower.Contains(" he ") && textLower.Contains(" she ");
            var hasThoughtMarkers = textLower.Contains("thought") || textLower.Contains("felt") ||
                                    textLower.Contains("wondered") || textLower.Contains("knew");

            if (hasMultipleSubjects && hasThoughtMarkers)
            {
                var confidence = Math.Min(1.0, thirdPersonDensity * 15);
                return ("omniscient", Math.Round(Math.Max(0.5, confidence), 4));
            }

            var thirdConfidence = Math.Min(1.0, thirdPersonDensity * 15);
            return ("third-person", Math.Round(Math.Max(0.6, thirdConfidence), 4));
        }

        // Mixed or unclear
        if (firstPersonDensity > 0.02 || thirdPersonDensity > 0.01)
        {
            var dominant = firstPersonDensity > thirdPersonDensity ? "first-person" : "third-person";
            return (dominant, 0.4);
        }

        return ("unknown", 0.0);
    }
}

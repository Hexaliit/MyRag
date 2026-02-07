using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Technical.Extractors;

/// <summary>
///     Extracts inline citations from academic text.
///     Handles numbered [1], [2-4], [1,3,5] and author-year (Smith, 2020), Smith et al. (2020) patterns.
/// </summary>
public static partial class CitationExtractor
{
    // Numbered citations: [1], [2,3], [1-4], [1, 2, 3]
    [GeneratedRegex(@"\[(\d+(?:\s*[-–,]\s*\d+)*)\]")]
    private static partial Regex NumberedCitationRegex();

    // Author-year in parentheses: (Smith, 2020), (Smith & Jones, 2020), (Smith et al., 2020)
    [GeneratedRegex(
        @"\(([A-Z][a-z]+(?:\s+(?:&|and)\s+[A-Z][a-z]+)?(?:\s+et\s+al\.?)?),?\s*(\d{4}[a-z]?)\)",
        RegexOptions.None)]
    private static partial Regex AuthorYearParensRegex();

    // Textual author-year: Smith (2020), Smith et al. (2020)
    [GeneratedRegex(
        @"([A-Z][a-z]+(?:\s+et\s+al\.?))\s*\((\d{4}[a-z]?)\)",
        RegexOptions.None)]
    private static partial Regex TextualAuthorYearRegex();

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();
        if (string.IsNullOrWhiteSpace(text)) return entities;

        // Numbered citations
        foreach (Match match in NumberedCitationRegex().Matches(text))
        {
            var inner = match.Groups[1].Value;
            var numbers = ExpandCitationRange(inner);

            foreach (var num in numbers)
            {
                entities.Add(new DomainEntity(
                    Text: match.Value,
                    EntityType: "citation_numbered",
                    DomainId: "technical",
                    StartOffset: match.Index,
                    EndOffset: match.Index + match.Length,
                    Confidence: 0.85,
                    Metadata: new Dictionary<string, object?>
                    {
                        ["citation_key"] = num.ToString(),
                        ["citation_style"] = "numbered"
                    }));
            }
        }

        // Author-year in parentheses
        foreach (Match match in AuthorYearParensRegex().Matches(text))
        {
            var author = match.Groups[1].Value.Trim();
            var year = match.Groups[2].Value;

            entities.Add(new DomainEntity(
                Text: match.Value,
                EntityType: "citation_author_year",
                DomainId: "technical",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.9,
                Metadata: new Dictionary<string, object?>
                {
                    ["citation_key"] = $"{author}_{year}",
                    ["author"] = author,
                    ["year"] = year,
                    ["citation_style"] = "author_year"
                }));
        }

        // Textual author-year
        foreach (Match match in TextualAuthorYearRegex().Matches(text))
        {
            var author = match.Groups[1].Value.Trim();
            var year = match.Groups[2].Value;

            // Skip if already captured by parens pattern
            var alreadyCaptured = entities.Any(e =>
                e.StartOffset <= match.Index && e.EndOffset >= match.Index + match.Length);
            if (alreadyCaptured) continue;

            entities.Add(new DomainEntity(
                Text: match.Value,
                EntityType: "citation_author_year",
                DomainId: "technical",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.85,
                Metadata: new Dictionary<string, object?>
                {
                    ["citation_key"] = $"{author}_{year}",
                    ["author"] = author,
                    ["year"] = year,
                    ["citation_style"] = "author_year"
                }));
        }

        return entities;
    }

    /// <summary>
    ///     Expand citation ranges like "1-4" to [1,2,3,4] and "1,3,5" to [1,3,5].
    /// </summary>
    public static List<int> ExpandCitationRange(string input)
    {
        var numbers = new List<int>();
        var parts = input.Split(',', StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            // Check for range (1-4 or 1–4)
            var rangeParts = part.Split(['-', '–'], StringSplitOptions.TrimEntries);
            if (rangeParts.Length == 2 &&
                int.TryParse(rangeParts[0], out var start) &&
                int.TryParse(rangeParts[1], out var end))
            {
                for (var i = start; i <= end && i <= start + 50; i++)
                    numbers.Add(i);
            }
            else if (int.TryParse(part.Trim(), out var num))
            {
                numbers.Add(num);
            }
        }

        return numbers;
    }
}

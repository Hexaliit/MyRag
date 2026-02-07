using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Technical.Extractors;

/// <summary>
///     Extracts bibliography/reference entries from academic text.
///     Detects the References/Bibliography section and parses individual entries.
/// </summary>
public static partial class BibliographyExtractor
{
    // Section header detection
    [GeneratedRegex(@"^(?:References|Bibliography|Works\s+Cited)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeaderRegex();

    // Numbered bibliography entry: [1] Author(s)... text
    [GeneratedRegex(@"^\[(\d+)\]\s*(.+?)(?=^\[\d+\]|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex NumberedEntryRegex();

    // Year extraction from entry
    [GeneratedRegex(@"\b((?:19|20)\d{2}[a-z]?)\b")]
    private static partial Regex YearRegex();

    // DOI in entry
    [GeneratedRegex(@"10\.\d{4,}/[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex DoiRegex();

    // arXiv in entry
    [GeneratedRegex(@"arXiv:\s*(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ArxivRegex();

    // Author name patterns at start of entry (basic heuristic)
    [GeneratedRegex(@"^([A-Z][a-z]+(?:,\s*[A-Z]\.?\s*)+(?:,?\s*(?:and|&)\s*[A-Z][a-z]+(?:,\s*[A-Z]\.?\s*)+)*)",
        RegexOptions.None)]
    private static partial Regex AuthorRegex();

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();
        if (string.IsNullOrWhiteSpace(text)) return entities;

        // Find the references section
        var refSection = ExtractReferenceSection(text);
        if (string.IsNullOrWhiteSpace(refSection)) return entities;

        // Try numbered entries first
        var matches = NumberedEntryRegex().Matches(refSection);
        if (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                var entryNumber = match.Groups[1].Value;
                var entryText = match.Groups[2].Value.Trim();
                var entity = ParseEntry(entryNumber, entryText);
                entities.Add(entity);
            }
        }
        else
        {
            // Fallback: split by blank lines or line-initial patterns
            var lines = refSection.Split('\n');
            var currentEntry = new List<string>();
            var entryNum = 1;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    if (currentEntry.Count > 0)
                    {
                        var entryText = string.Join(" ", currentEntry).Trim();
                        if (entryText.Length > 10)
                            entities.Add(ParseEntry(entryNum.ToString(), entryText));
                        entryNum++;
                        currentEntry.Clear();
                    }
                }
                else
                {
                    currentEntry.Add(trimmed);
                }
            }

            // Don't forget the last entry
            if (currentEntry.Count > 0)
            {
                var entryText = string.Join(" ", currentEntry).Trim();
                if (entryText.Length > 10)
                    entities.Add(ParseEntry(entryNum.ToString(), entryText));
            }
        }

        return entities;
    }

    /// <summary>
    ///     Detect whether text contains a bibliography section.
    /// </summary>
    public static bool HasBibliography(string text)
    {
        return SectionHeaderRegex().IsMatch(text);
    }

    private static string? ExtractReferenceSection(string text)
    {
        var match = SectionHeaderRegex().Match(text);
        if (!match.Success) return null;

        // Take everything after the header
        var startIndex = match.Index + match.Length;
        if (startIndex >= text.Length) return null;

        return text[startIndex..];
    }

    private static DomainEntity ParseEntry(string entryNumber, string entryText)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["entry_number"] = entryNumber
        };

        // Extract year
        var yearMatch = YearRegex().Match(entryText);
        if (yearMatch.Success)
            metadata["year"] = yearMatch.Groups[1].Value;

        // Extract DOI
        var doiMatch = DoiRegex().Match(entryText);
        if (doiMatch.Success)
            metadata["doi"] = doiMatch.Value;

        // Extract arXiv ID
        var arxivMatch = ArxivRegex().Match(entryText);
        if (arxivMatch.Success)
            metadata["arxiv_id"] = arxivMatch.Groups[1].Value;

        // Extract authors (best effort)
        var authorMatch = AuthorRegex().Match(entryText);
        if (authorMatch.Success)
            metadata["authors"] = authorMatch.Value;

        // Title heuristic: text between authors/year and the first period after year
        // This is inherently approximate for bibliography parsing
        var title = ExtractTitle(entryText, yearMatch);
        if (!string.IsNullOrEmpty(title))
            metadata["title"] = title;

        return new DomainEntity(
            Text: entryText.Length > 200 ? entryText[..200] + "..." : entryText,
            EntityType: "bibliography_entry",
            DomainId: "technical",
            StartOffset: 0,
            EndOffset: entryText.Length,
            Confidence: 0.8,
            Metadata: metadata);
    }

    private static string? ExtractTitle(string entryText, Match yearMatch)
    {
        if (!yearMatch.Success) return null;

        // Look for text after year, up to next period
        var afterYear = yearMatch.Index + yearMatch.Length;
        if (afterYear >= entryText.Length) return null;

        var rest = entryText[afterYear..].TrimStart(' ', '.', ',', ')');
        var periodIdx = rest.IndexOf('.');
        if (periodIdx > 5 && periodIdx < 300)
            return rest[..periodIdx].Trim();

        // If no good period, take first 200 chars
        return rest.Length > 200 ? rest[..200].Trim() : rest.Trim();
    }
}

using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Financial.Extractors;

/// <summary>
///     Extracts currency amounts from text.
///     Handles $1.2M, EUR 500K, 1,234.56, etc.
/// </summary>
public static partial class AmountExtractor
{
    // Match $1,234.56 or $1.2M/B/K style
    [GeneratedRegex(@"\$[\d,]+\.?\d*\s*[MBKmTt]?(?:illion|illion)?\b", RegexOptions.Compiled)]
    private static partial Regex DollarAmountRegex();

    // Match EUR/GBP/JPY/CHF amounts: EUR 500K, GBP 1.2M
    [GeneratedRegex(@"\b(EUR|GBP|JPY|CHF|CAD|AUD|CNY|INR)\s*[\d,]+\.?\d*\s*[MBKmTt]?(?:illion)?\b",
        RegexOptions.Compiled)]
    private static partial Regex CurrencyAmountRegex();

    // Match plain number with magnitude: 1.2 billion, 500 million, $142B
    [GeneratedRegex(@"\b[\d,]+\.?\d*\s*(?:billion|million|trillion|thousand|[BMKTbmkt])\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MagnitudeAmountRegex();

    // Match percentage: 12.5%, +3.2%, -1.5%
    [GeneratedRegex(@"[+-]?\d+\.?\d*\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentageRegex();

    public static List<DomainEntity> ExtractAmounts(string text)
    {
        var entities = new List<DomainEntity>();

        foreach (Match match in DollarAmountRegex().Matches(text))
        {
            entities.Add(new DomainEntity(
                Text: match.Value.Trim(),
                EntityType: "amount",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.90,
                Metadata: new Dictionary<string, object?> { ["currency"] = "USD" }));
        }

        foreach (Match match in CurrencyAmountRegex().Matches(text))
        {
            var currency = match.Groups[1].Value;
            entities.Add(new DomainEntity(
                Text: match.Value.Trim(),
                EntityType: "amount",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.88,
                Metadata: new Dictionary<string, object?> { ["currency"] = currency }));
        }

        return entities;
    }

    public static List<DomainEntity> ExtractPercentages(string text)
    {
        var entities = new List<DomainEntity>();

        foreach (Match match in PercentageRegex().Matches(text))
        {
            entities.Add(new DomainEntity(
                Text: match.Value.Trim(),
                EntityType: "percentage",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.92));
        }

        return entities;
    }
}

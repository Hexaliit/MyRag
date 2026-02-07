using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Financial.Extractors;

/// <summary>
///     Extracts financial date references from text.
///     Handles Q1 2025, FY2024, fiscal year, H1 2025, etc.
/// </summary>
public static partial class FinancialDateExtractor
{
    // Fiscal quarters: Q1 2025, Q4 FY2024, 1Q2025
    [GeneratedRegex(@"\b(?:Q[1-4]\s*(?:FY)?\s*\d{4}|[1-4]Q\s*\d{4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex QuarterRegex();

    // Fiscal year: FY2024, FY 2024, fiscal year 2024
    [GeneratedRegex(@"\b(?:FY\s*\d{4}|fiscal\s+year\s+\d{4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FiscalYearRegex();

    // Half-year: H1 2025, H2 2024, 1H2025
    [GeneratedRegex(@"\b(?:H[12]\s*\d{4}|[12]H\s*\d{4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HalfYearRegex();

    // Year-to-date, month-to-date etc.
    [GeneratedRegex(@"\b(?:YTD|MTD|QTD)\b", RegexOptions.Compiled)]
    private static partial Regex PeriodAbbreviationRegex();

    // SEC filing dates: 10-K, 10-Q, 8-K, S-1
    [GeneratedRegex(@"\b(?:10-[KQ]|8-K|S-[14]|20-F|6-K)\b", RegexOptions.Compiled)]
    private static partial Regex SecFilingRegex();

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();

        foreach (Match match in QuarterRegex().Matches(text))
        {
            entities.Add(new DomainEntity(
                Text: match.Value,
                EntityType: "financial_date",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.88,
                Metadata: new Dictionary<string, object?> { ["date_type"] = "quarter" }));
        }

        foreach (Match match in FiscalYearRegex().Matches(text))
        {
            entities.Add(new DomainEntity(
                Text: match.Value,
                EntityType: "financial_date",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.90,
                Metadata: new Dictionary<string, object?> { ["date_type"] = "fiscal_year" }));
        }

        foreach (Match match in HalfYearRegex().Matches(text))
        {
            entities.Add(new DomainEntity(
                Text: match.Value,
                EntityType: "financial_date",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.85,
                Metadata: new Dictionary<string, object?> { ["date_type"] = "half_year" }));
        }

        foreach (Match match in SecFilingRegex().Matches(text))
        {
            entities.Add(new DomainEntity(
                Text: match.Value,
                EntityType: "instrument",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.95,
                Metadata: new Dictionary<string, object?> { ["instrument_type"] = "sec_filing" }));
        }

        return entities;
    }
}

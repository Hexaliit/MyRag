using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Financial.Extractors;

/// <summary>
///     Extracts stock ticker symbols from text.
///     Handles $AAPL, AAPL, NASDAQ:AAPL patterns.
/// </summary>
public static partial class TickerExtractor
{
    // Match $AAPL or $MSFT style (dollar sign prefix)
    [GeneratedRegex(@"\$([A-Z]{1,5})\b", RegexOptions.Compiled)]
    private static partial Regex DollarTickerRegex();

    // Match EXCHANGE:TICKER style (e.g., NASDAQ:AAPL, NYSE:GS)
    [GeneratedRegex(@"\b(NYSE|NASDAQ|LSE|TSE|HKEX):([A-Z]{1,5})\b", RegexOptions.Compiled)]
    private static partial Regex ExchangeTickerRegex();

    // Common financial terms that look like tickers but aren't
    private static readonly HashSet<string> FalsePositives = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "I", "AM", "AN", "AS", "AT", "BE", "BY", "DO", "GO", "HE", "IF", "IN",
        "IS", "IT", "ME", "MY", "NO", "OF", "OK", "ON", "OR", "SO", "TO", "UP", "US",
        "WE", "CEO", "CFO", "CTO", "COO", "IPO", "ETF", "SEC", "GDP", "CPI", "FBI",
        "USA", "UK", "EU", "USD", "EUR", "GBP", "JPY", "THE", "AND", "FOR", "NOT",
        "BUT", "ALL", "CAN", "HER", "WAS", "ONE", "OUR", "OUT", "DAY", "HAD", "HAS",
        "HOW", "ITS", "MAY", "NEW", "NOW", "OLD", "SEE", "WAY", "WHO", "DID", "GET",
        "HIM", "LET", "SAY", "SHE", "TOO", "USE", "ALSO", "YEAR", "OVER", "SUCH",
        "SAID", "THIS", "THAT", "WITH", "FROM", "HAVE", "BEEN", "WERE", "THEM",
        "WILL", "WHAT", "WHEN", "MAKE", "LIKE", "THAN", "EACH", "JUST", "ONLY",
        "COME", "MADE", "FIND", "HERE", "KNOW", "TAKE", "WANT", "DOES", "LONG",
        "VERY", "MUCH", "MORE", "MOST", "SAME", "SOME", "PART", "WELL", "BACK",
        "HIGH", "LAST", "INTO", "OVER", "DOWN", "EVEN", "STILL", "EVERY"
    };

    public static List<DomainEntity> Extract(string text)
    {
        var entities = new List<DomainEntity>();

        // Pattern 1: $AAPL
        foreach (Match match in DollarTickerRegex().Matches(text))
        {
            var ticker = match.Groups[1].Value;
            if (!FalsePositives.Contains(ticker))
                entities.Add(new DomainEntity(
                    Text: $"${ticker}",
                    EntityType: "ticker",
                    DomainId: "financial",
                    StartOffset: match.Index,
                    EndOffset: match.Index + match.Length,
                    Confidence: 0.95));
        }

        // Pattern 2: EXCHANGE:TICKER
        foreach (Match match in ExchangeTickerRegex().Matches(text))
        {
            var exchange = match.Groups[1].Value;
            var ticker = match.Groups[2].Value;
            entities.Add(new DomainEntity(
                Text: $"{exchange}:{ticker}",
                EntityType: "ticker",
                DomainId: "financial",
                StartOffset: match.Index,
                EndOffset: match.Index + match.Length,
                Confidence: 0.98,
                Metadata: new Dictionary<string, object?> { ["exchange"] = exchange }));
        }

        return entities;
    }
}

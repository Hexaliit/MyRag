using DomainClassifier.Core.Models;

namespace DomainClassifier.Financial;

/// <summary>
///     Fast keyword-based classifier for financial content.
///     No model needed - uses weighted keyword matching.
/// </summary>
public static class FinancialKeywordClassifier
{
    // High-confidence financial terms (weighted 3x)
    private static readonly HashSet<string> StrongTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "revenue", "earnings", "dividend", "EBITDA", "EPS", "P/E", "market cap",
        "fiscal year", "quarterly", "annual report", "10-K", "10-Q", "8-K",
        "balance sheet", "income statement", "cash flow", "profit margin",
        "gross margin", "operating margin", "net income", "depreciation",
        "amortization", "shareholder", "stockholder", "common stock",
        "preferred stock", "treasury stock", "stock buyback", "share repurchase",
        "IPO", "secondary offering", "venture capital", "private equity",
        "hedge fund", "mutual fund", "ETF", "index fund",
        "bond yield", "credit rating", "default risk", "credit spread",
        "interest rate", "Federal Reserve", "monetary policy", "inflation rate",
        "GDP growth", "unemployment rate", "consumer spending",
        "bull market", "bear market", "volatility", "VIX",
        "S&P 500", "Dow Jones", "NASDAQ", "Russell 2000",
        "options", "futures", "derivatives", "swap", "forward contract",
        "underwriter", "fiduciary", "SEC filing", "prospectus",
        "warrant", "convertible", "callable", "maturity date",
        "yield to maturity", "coupon rate", "face value", "par value",
        "leveraged buyout", "merger", "acquisition", "divestiture"
    };

    // Medium-confidence financial terms (weighted 1x)
    private static readonly HashSet<string> ModerateTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "stock", "share", "equity", "bond", "asset", "liability",
        "investment", "portfolio", "return", "risk", "capital",
        "market", "trading", "exchange", "broker", "fund",
        "valuation", "price", "cost", "profit", "loss",
        "growth", "decline", "increase", "decrease",
        "financial", "fiscal", "monetary", "economic",
        "bank", "banking", "lender", "borrower", "credit",
        "debt", "loan", "mortgage", "insurance", "pension",
        "analyst", "forecast", "guidance", "outlook", "target",
        "sector", "industry", "benchmark", "performance",
        "allocation", "diversification", "hedge", "liquidity"
    };

    /// <summary>
    ///     Classify text as financial content based on keyword density.
    ///     Returns confidence from 0.0 to 1.0.
    /// </summary>
    public static DomainClassification Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DomainClassification("financial", "Financial Domain", 0.0);

        var textLower = text.ToLowerInvariant();
        var words = textLower.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}'],
            StringSplitOptions.RemoveEmptyEntries);

        var totalWords = Math.Max(words.Length, 1);
        var strongHits = 0;
        var moderateHits = 0;

        // Check multi-word terms in full text
        foreach (var term in StrongTerms)
            if (textLower.Contains(term.ToLowerInvariant()))
                strongHits++;

        foreach (var term in ModerateTerms)
            if (textLower.Contains(term.ToLowerInvariant()))
                moderateHits++;

        // Weighted score: strong terms count 3x
        var weightedScore = (strongHits * 3.0 + moderateHits) / totalWords;

        // Normalize to 0-1 range using a sigmoid-like curve
        // Empirically: 2+ strong hits in 100 words → ~0.7 confidence
        var confidence = Math.Min(1.0, weightedScore * 10.0);

        // Floor: at least 2 strong terms needed for any meaningful confidence
        if (strongHits < 2)
            confidence = Math.Min(confidence, 0.4);

        return new DomainClassification(
            "financial",
            "Financial Domain",
            Math.Round(confidence, 4));
    }
}

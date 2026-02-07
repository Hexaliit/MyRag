using DomainClassifier.Financial;

namespace DomainClassifier.Tests;

public class FinancialKeywordClassifierTests
{
    [Fact]
    public void Classify_FinancialText_HighConfidence()
    {
        var text = "The company reported quarterly earnings of $2.5B with revenue growth of 15%. " +
                   "EBITDA margins improved and the balance sheet remains strong. " +
                   "The board approved a dividend increase.";

        var result = FinancialKeywordClassifier.Classify(text);

        Assert.Equal("financial", result.DomainId);
        Assert.True(result.Confidence > 0.6,
            $"Expected confidence > 0.6 for financial text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_NonFinancialText_LowConfidence()
    {
        var text = "The cat sat on the mat. It was a sunny day and the birds were singing. " +
                   "The children played in the park while their parents watched from the bench.";

        var result = FinancialKeywordClassifier.Classify(text);

        Assert.Equal("financial", result.DomainId);
        Assert.True(result.Confidence < 0.4,
            $"Expected confidence < 0.4 for non-financial text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_EmptyText_ZeroConfidence()
    {
        var result = FinancialKeywordClassifier.Classify("");

        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Classify_EarningsReport_HighConfidence()
    {
        var text = "Apple Inc. reported fiscal year 2024 annual report showing net income of $94B. " +
                   "The company's P/E ratio stands at 28x with EPS of $6.13. " +
                   "Cash flow from operations increased 10% year over year.";

        var result = FinancialKeywordClassifier.Classify(text);

        Assert.True(result.Confidence > 0.7,
            $"Expected confidence > 0.7 for earnings report, got {result.Confidence}");
    }
}

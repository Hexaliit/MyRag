using DomainClassifier.Financial.Extractors;

namespace DomainClassifier.Tests;

public class FinancialDateExtractorTests
{
    [Theory]
    [InlineData("Q1 2025", "financial_date")]
    [InlineData("Q4 FY2024", "financial_date")]
    [InlineData("FY2024", "financial_date")]
    [InlineData("fiscal year 2024", "financial_date")]
    [InlineData("H1 2025", "financial_date")]
    public void Extract_FinancialDates_ReturnsEntities(string input, string expectedType)
    {
        var entities = FinancialDateExtractor.Extract($"Results for {input} were strong");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e => e.EntityType == expectedType);
    }

    [Theory]
    [InlineData("10-K")]
    [InlineData("10-Q")]
    [InlineData("8-K")]
    [InlineData("S-1")]
    public void Extract_SecFilings_ReturnsInstrumentEntities(string filing)
    {
        var entities = FinancialDateExtractor.Extract($"The company filed a {filing} with the SEC");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e => e.EntityType == "instrument");
        Assert.Contains(entities, e => e.Metadata?["instrument_type"]?.ToString() == "sec_filing");
    }

    [Fact]
    public void Extract_NoFinancialDates_ReturnsEmpty()
    {
        var entities = FinancialDateExtractor.Extract("It was a sunny day in March");

        Assert.Empty(entities);
    }
}

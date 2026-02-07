using DomainClassifier.Financial.Extractors;

namespace DomainClassifier.Tests;

public class AmountExtractorTests
{
    [Fact]
    public void ExtractAmounts_DollarAmount_ReturnsEntity()
    {
        var entities = AmountExtractor.ExtractAmounts("Revenue was $142B last quarter");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e => e.EntityType == "amount");
        Assert.Contains(entities, e => e.Metadata?["currency"]?.ToString() == "USD");
    }

    [Fact]
    public void ExtractAmounts_EurAmount_ReturnsWithCurrency()
    {
        var entities = AmountExtractor.ExtractAmounts("Total was EUR 500K in revenue");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e => e.Metadata?["currency"]?.ToString() == "EUR");
    }

    [Fact]
    public void ExtractPercentages_ReturnsEntity()
    {
        var entities = AmountExtractor.ExtractPercentages("Growth of 12.5% year over year");

        Assert.Single(entities);
        Assert.Equal("percentage", entities[0].EntityType);
        Assert.Contains("12.5%", entities[0].Text);
    }

    [Fact]
    public void ExtractPercentages_NegativePercentage_ReturnsEntity()
    {
        var entities = AmountExtractor.ExtractPercentages("Revenue declined -3.2% this quarter");

        Assert.Single(entities);
        Assert.Contains("-3.2%", entities[0].Text);
    }

    [Fact]
    public void ExtractAmounts_NoAmounts_ReturnsEmpty()
    {
        var entities = AmountExtractor.ExtractAmounts("The cat sat on the mat");

        Assert.Empty(entities);
    }
}

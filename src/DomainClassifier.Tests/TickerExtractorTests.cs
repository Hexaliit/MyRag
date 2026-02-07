using DomainClassifier.Financial.Extractors;

namespace DomainClassifier.Tests;

public class TickerExtractorTests
{
    [Fact]
    public void Extract_DollarTicker_ReturnsEntity()
    {
        var entities = TickerExtractor.Extract("$AAPL rose 5% today");

        Assert.Single(entities);
        Assert.Equal("$AAPL", entities[0].Text);
        Assert.Equal("ticker", entities[0].EntityType);
        Assert.Equal("financial", entities[0].DomainId);
        Assert.True(entities[0].Confidence >= 0.9);
    }

    [Fact]
    public void Extract_MultipleTickers_ReturnsAll()
    {
        var entities = TickerExtractor.Extract("$AAPL and $MSFT both reported strong earnings");

        Assert.Equal(2, entities.Count);
        Assert.Contains(entities, e => e.Text == "$AAPL");
        Assert.Contains(entities, e => e.Text == "$MSFT");
    }

    [Fact]
    public void Extract_ExchangeTicker_ReturnsWithExchange()
    {
        var entities = TickerExtractor.Extract("NASDAQ:TSLA hit new highs");

        Assert.Single(entities);
        Assert.Equal("NASDAQ:TSLA", entities[0].Text);
        Assert.Equal("ticker", entities[0].EntityType);
        Assert.NotNull(entities[0].Metadata);
        Assert.Equal("NASDAQ", entities[0].Metadata!["exchange"]);
    }

    [Fact]
    public void Extract_NoTickers_ReturnsEmpty()
    {
        var entities = TickerExtractor.Extract("The weather is nice today");

        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_FalsePositives_AreFiltered()
    {
        // "CEO" and "IPO" should not be detected as tickers via $-prefix pattern
        var entities = TickerExtractor.Extract("The CEO announced the IPO");

        Assert.Empty(entities);
    }
}

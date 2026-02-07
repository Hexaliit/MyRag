using DomainClassifier.Core.Models;

namespace DomainClassifier.Tests;

public class DomainEnrichmentResultTests
{
    [Fact]
    public void ToMetadata_WithClassificationsAndEntities_ReturnsExpectedKeys()
    {
        var result = new DomainEnrichmentResult(
            Classifications:
            [
                new DomainClassification("financial", "Financial Domain", 0.87)
            ],
            Entities:
            [
                new DomainEntity("$AAPL", "ticker", "financial", 0, 5, 0.95),
                new DomainEntity("$142B", "amount", "financial", 10, 15, 0.90)
            ],
            Signals:
            [
                new DomainSignal("financial.sentiment", "positive", 0.92, "financial")
            ]);

        var metadata = result.ToMetadata();

        Assert.Equal("financial", metadata["domain.detected"]);
        Assert.Equal(0.87, metadata["domain.confidence"]);
        Assert.Equal("positive", metadata["domain.financial.sentiment"]);
        Assert.True(metadata.ContainsKey("domain.financial.entities"));
    }

    [Fact]
    public void ToMetadata_EmptyResult_ReturnsEmptyDictionary()
    {
        var metadata = DomainEnrichmentResult.Empty.ToMetadata();

        Assert.Empty(metadata);
    }

    [Fact]
    public void Empty_IsSingleton()
    {
        Assert.Same(DomainEnrichmentResult.Empty, DomainEnrichmentResult.Empty);
    }
}

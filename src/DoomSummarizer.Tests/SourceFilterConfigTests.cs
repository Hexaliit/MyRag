using System.Text.Json;
using DoomSummarizer.Models;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for SourceFilterConfig serialization and default values.
/// </summary>
public class SourceFilterConfigTests
{
    [Fact]
    public void DefaultConfig_HasExpectedWeights()
    {
        var config = new SourceFilterConfig
        {
            Weights = new Dictionary<string, double>
            {
                ["reuters"] = 1.4,
                ["bbc"] = 1.3,
                ["reddit"] = 0.9,
                ["search"] = 0.8
            }
        };

        config.Weights["reuters"].Should().Be(1.4);
        config.Weights["search"].Should().Be(0.8);
    }

    [Fact]
    public void DefaultConfig_HasEmptyDomainLists()
    {
        var config = new SourceFilterConfig();
        config.AllowedDomains.Should().BeEmpty();
        config.BlockedDomains.Should().BeEmpty();
    }

    [Fact]
    public void SourceFilterConfig_RoundTripsJson()
    {
        var original = new SourceFilterConfig
        {
            AllowedDomains = ["bbc.com", "reuters.com"],
            BlockedDomains = ["facebook.com"],
            Weights = new Dictionary<string, double>
            {
                ["hn"] = 1.1,
                ["reddit"] = 0.9
            }
        };

        var json = JsonSerializer.Serialize(original, DoomConfigContext.Default.SourceFilterConfig);
        var deserialized = JsonSerializer.Deserialize(json, DoomConfigContext.Default.SourceFilterConfig);

        deserialized.Should().NotBeNull();
        deserialized!.AllowedDomains.Should().BeEquivalentTo(original.AllowedDomains);
        deserialized.BlockedDomains.Should().BeEquivalentTo(original.BlockedDomains);
        deserialized.Weights.Should().BeEquivalentTo(original.Weights);
    }

    [Fact]
    public void QueryMatch_StoresAllFields()
    {
        var match = new DoomSummarizer.Services.QueryMatch
        {
            QueryId = 42,
            QueryText = "funny onion story",
            Vibe = "snarky",
            ItemIds = ["item1", "item2", "item3"],
            IssuedAt = DateTimeOffset.UtcNow,
            Similarity = 0.95f
        };

        match.QueryId.Should().Be(42);
        match.QueryText.Should().Be("funny onion story");
        match.Vibe.Should().Be("snarky");
        match.ItemIds.Should().HaveCount(3);
        match.Similarity.Should().BeApproximately(0.95f, 0.001f);
    }
}

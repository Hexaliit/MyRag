using DoomSummarizer.Plugins;

namespace DoomSummarizer.Tests.Plugins;

public class SourceFetchContextTests
{
    [Theory]
    [InlineData("hn", "hn")]
    [InlineData("HN", "hn")]
    [InlineData("reddit", "reddit")]
    [InlineData("REDDIT", "reddit")]
    public void Parse_ExtractsKey(string input, string expectedKey)
    {
        var ctx = SourceFetchContext.Parse(input);
        ctx.SourceKey.Should().Be(expectedKey);
    }

    [Fact]
    public void Parse_SimpleKey_NoSubParams()
    {
        var ctx = SourceFetchContext.Parse("hn");
        ctx.SourceKey.Should().Be("hn");
        ctx.SubParams.Should().BeEmpty();
        ctx.RawSource.Should().Be("hn");
    }

    [Fact]
    public void Parse_KeyWithOneParam()
    {
        var ctx = SourceFetchContext.Parse("reddit:csharp");
        ctx.SourceKey.Should().Be("reddit");
        ctx.SubParams.Should().Equal("csharp");
    }

    [Fact]
    public void Parse_KeyWithMultipleParams()
    {
        var ctx = SourceFetchContext.Parse("arxiv:cat:cs.AI");
        ctx.SourceKey.Should().Be("arxiv");
        ctx.SubParams.Should().Equal("cat", "cs.ai");
    }

    [Fact]
    public void Parse_PreservesOptionalFields()
    {
        var ctx = SourceFetchContext.Parse("hn", query: "test", limit: 50, vibe: "doom");
        ctx.Query.Should().Be("test");
        ctx.Limit.Should().Be(50);
        ctx.Vibe.Should().Be("doom");
    }

    [Fact]
    public void ParseWithCompositeKeys_MatchesLongestKey()
    {
        var registeredKeys = new[] { "brave", "brave_search", "brave_news", "gnews", "gnews_topic" };

        var ctx = SourceFetchContext.ParseWithCompositeKeys(
            "gnews_topic:HEALTH", registeredKeys);

        ctx.SourceKey.Should().Be("gnews_topic");
        ctx.SubParams.Should().Equal("health");
    }

    [Fact]
    public void ParseWithCompositeKeys_BraveSearch_MatchesFullKey()
    {
        var registeredKeys = new[] { "brave", "brave_search", "brave_news" };

        var ctx = SourceFetchContext.ParseWithCompositeKeys(
            "brave_search:AI agents", registeredKeys);

        ctx.SourceKey.Should().Be("brave_search");
        ctx.SubParams.Should().Equal("ai agents");
    }

    [Fact]
    public void ParseWithCompositeKeys_SimpleKey_FallsBack()
    {
        var registeredKeys = new[] { "brave", "brave_search" };

        var ctx = SourceFetchContext.ParseWithCompositeKeys(
            "brave:some query", registeredKeys);

        ctx.SourceKey.Should().Be("brave");
        ctx.SubParams.Should().Equal("some query");
    }

    [Fact]
    public void ParseWithCompositeKeys_UnknownKey_SplitsOnColon()
    {
        var registeredKeys = new[] { "hn", "reddit" };

        var ctx = SourceFetchContext.ParseWithCompositeKeys(
            "unknown:param1:param2", registeredKeys);

        ctx.SourceKey.Should().Be("unknown");
        ctx.SubParams.Should().Equal("param1", "param2");
    }

    [Fact]
    public void ParseWithCompositeKeys_BareKey_NoSubParams()
    {
        var registeredKeys = new[] { "hn", "reddit", "gnews_topic" };

        var ctx = SourceFetchContext.ParseWithCompositeKeys(
            "gnews_topic", registeredKeys);

        ctx.SourceKey.Should().Be("gnews_topic");
        ctx.SubParams.Should().BeEmpty();
    }

    [Fact]
    public void Parse_HttpUrl_KeyIsFullUrl()
    {
        var ctx = SourceFetchContext.Parse("https://example.com/feed.xml");
        // With simple parse, key is "https" (split on colon)
        ctx.SourceKey.Should().Be("https");
    }

    [Fact]
    public void ParseWithCompositeKeys_HttpUrl_KeyIsHttp()
    {
        var registeredKeys = new[] { "hn", "web" };

        var ctx = SourceFetchContext.ParseWithCompositeKeys(
            "https://example.com/feed.xml", registeredKeys);

        // URL not in registered keys — falls back to simple split
        ctx.SourceKey.Should().Be("https");
    }
}

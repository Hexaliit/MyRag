using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for UrlFixerService - Google News and aggregator URL resolution.
/// </summary>
public class UrlFixerServiceTests
{
    [Theory]
    [InlineData("https://news.google.com/rss/articles/CBMiXWh0dHBz", true)]
    [InlineData("https://news.google.com/articles/CBMiXWh0dHBz", true)]
    [InlineData("https://google.com/url?q=https://example.com", true)]
    [InlineData("https://bing.com/news/apiclick?url=https://example.com", true)]
    [InlineData("https://www.bbc.com/news/article", false)]
    [InlineData("https://example.com/page", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void NeedsFix_DetectsAggregatorUrls(string? url, bool expected)
    {
        UrlFixerService.NeedsFix(url).Should().Be(expected);
    }

    [Fact]
    public async Task FixUrlAsync_NonAggregatorUrl_ReturnsOriginal()
    {
        var httpClient = new HttpClient();
        var fixer = new UrlFixerService(httpClient);

        var url = "https://www.example.com/article";
        var result = await fixer.FixUrlAsync(url);

        result.Should().Be(url);
    }

    [Fact]
    public async Task FixUrlsBatchAsync_EmptyList_ReturnsEmpty()
    {
        var httpClient = new HttpClient();
        var fixer = new UrlFixerService(httpClient);

        var result = await fixer.FixUrlsBatchAsync([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FixUrlsBatchAsync_NonAggregatorUrls_ReturnsOriginals()
    {
        var httpClient = new HttpClient();
        var fixer = new UrlFixerService(httpClient);

        var urls = new[] { "https://example.com/1", "https://example.com/2" };
        var result = await fixer.FixUrlsBatchAsync(urls);

        result.Should().HaveCount(2);
        result["https://example.com/1"].Should().Be("https://example.com/1");
        result["https://example.com/2"].Should().Be("https://example.com/2");
    }

    [Fact]
    public async Task FixUrlAsync_CachesResults()
    {
        var httpClient = new HttpClient();
        var fixer = new UrlFixerService(httpClient);

        var url = "https://example.com/test";

        // First call
        var result1 = await fixer.FixUrlAsync(url);
        // Second call should use cache
        var result2 = await fixer.FixUrlAsync(url);

        result1.Should().Be(result2);
    }
}
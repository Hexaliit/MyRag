using System.Reflection;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for GoogleNewsFetcher static helper methods:
///     URL extraction from HTML, article ID parsing, URL validation.
///     These methods are private static — we use reflection to test them
///     since they contain the logic that previously caused IndexOutOfRange crashes.
/// </summary>
public class GoogleNewsFetcherTests
{
    private static readonly Type FetcherType = typeof(GoogleNewsFetcher);

    private static string? InvokeExtractUrlFromGoogleNewsHtml(string html)
    {
        var method = FetcherType.GetMethod("ExtractUrlFromGoogleNewsHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method?.Invoke(null, [html]);
    }

    private static string? InvokeExtractArticleId(string url)
    {
        var method = FetcherType.GetMethod("ExtractArticleId",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method?.Invoke(null, [url]);
    }

    private static bool InvokeIsValidArticleUrl(string? url)
    {
        var method = FetcherType.GetMethod("IsValidArticleUrl",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)(method?.Invoke(null, [url]) ?? false);
    }

    private static string? InvokeExtractUrlFromBatchResponse(string body)
    {
        var method = FetcherType.GetMethod("ExtractUrlFromBatchResponse",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method?.Invoke(null, [body]);
    }

    #region IsValidArticleUrl

    [Theory]
    [InlineData("https://www.bbc.com/news/article", true)]
    [InlineData("http://example.com/article", true)]
    [InlineData("https://news.google.com/stories", false)]
    [InlineData("https://consent.google.com/something", false)]
    [InlineData("https://accounts.google.com/login", false)]
    [InlineData("https://google.com/sorry/index", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("ftp://example.com/file", false)]
    [InlineData("not-a-url", false)]
    public void IsValidArticleUrl_VariousInputs(string? url, bool expected)
    {
        InvokeIsValidArticleUrl(url).Should().Be(expected);
    }

    #endregion

    #region ExtractUrlFromGoogleNewsHtml

    [Fact]
    public void ExtractUrlFromHtml_DataNauAttribute_ExtractsUrl()
    {
        var html = """<div data-n-au="https://www.reuters.com/article/12345">Article</div>""";
        var result = InvokeExtractUrlFromGoogleNewsHtml(html);
        result.Should().Be("https://www.reuters.com/article/12345");
    }

    [Fact]
    public void ExtractUrlFromHtml_AnchorTag_ExtractsUrl()
    {
        var html = """<a href="https://www.bbc.com/news/article-123" rel="noopener">Article</a>""";
        var result = InvokeExtractUrlFromGoogleNewsHtml(html);
        result.Should().Be("https://www.bbc.com/news/article-123");
    }

    [Fact]
    public void ExtractUrlFromHtml_JavaScriptRedirect_ExtractsUrl()
    {
        var html = """<script>window.location = "https://example.com/article";</script>""";
        var result = InvokeExtractUrlFromGoogleNewsHtml(html);
        result.Should().Be("https://example.com/article");
    }

    [Fact]
    public void ExtractUrlFromHtml_MetaRefresh_ExtractsUrl()
    {
        var html = """<meta http-equiv="refresh" content="0;url=https://example.com/article">""";
        var result = InvokeExtractUrlFromGoogleNewsHtml(html);
        result.Should().Be("https://example.com/article");
    }

    [Fact]
    public void ExtractUrlFromHtml_EmptyHtml_ReturnsNull()
    {
        InvokeExtractUrlFromGoogleNewsHtml("").Should().BeNull();
    }

    [Fact]
    public void ExtractUrlFromHtml_GoogleNewsUrl_SkipsIt()
    {
        var html = """<a href="https://news.google.com/stories/something">Link</a>""";
        InvokeExtractUrlFromGoogleNewsHtml(html).Should().BeNull();
    }

    [Fact]
    public void ExtractUrlFromHtml_NoValidUrls_ReturnsNull()
    {
        var html = """<div>No links here</div>""";
        InvokeExtractUrlFromGoogleNewsHtml(html).Should().BeNull();
    }

    #endregion

    #region ExtractArticleId

    [Fact]
    public void ExtractArticleId_ValidUrl_ReturnsId()
    {
        var url = "https://news.google.com/rss/articles/CBMiYmh0dHBzOi8vd3d3LmJiYy5jb20?oc=5";
        var result = InvokeExtractArticleId(url);
        result.Should().Be("CBMiYmh0dHBzOi8vd3d3LmJiYy5jb20");
    }

    [Fact]
    public void ExtractArticleId_NoArticlesPath_ReturnsNull()
    {
        InvokeExtractArticleId("https://news.google.com/rss/search?q=test").Should().BeNull();
    }

    [Fact]
    public void ExtractArticleId_ShortId_ReturnsNull()
    {
        InvokeExtractArticleId("https://news.google.com/rss/articles/abc").Should().BeNull();
    }

    #endregion

    #region ExtractUrlFromBatchResponse

    [Fact]
    public void ExtractUrlFromBatchResponse_ValidPayload_ExtractsUrl()
    {
        var body = """some prefix garturlres more data "https://www.reuters.com/article/test" end""";
        var result = InvokeExtractUrlFromBatchResponse(body);
        result.Should().Be("https://www.reuters.com/article/test");
    }

    [Fact]
    public void ExtractUrlFromBatchResponse_NoMarker_ReturnsNull()
    {
        InvokeExtractUrlFromBatchResponse("no garturlres marker here").Should().BeNull();
    }

    [Fact]
    public void ExtractUrlFromBatchResponse_EmptyBody_ReturnsNull()
    {
        InvokeExtractUrlFromBatchResponse("").Should().BeNull();
    }

    #endregion
}
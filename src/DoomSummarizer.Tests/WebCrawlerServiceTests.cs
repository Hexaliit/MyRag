using System.Reflection;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for WebCrawlerService static helpers: URL normalization, blocked extensions.
///     Uses reflection to access private static methods since they don't warrant being public.
/// </summary>
public class WebCrawlerServiceTests
{
    private static string NormalizeUrl(string url)
    {
        var method = typeof(WebCrawlerService).GetMethod("NormalizeUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [url])!;
    }

    private static bool IsBlockedExtension(string url)
    {
        var method = typeof(WebCrawlerService).GetMethod("IsBlockedExtension",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [url])!;
    }

    private static string TruncateUrl(string url, int maxLen)
    {
        var method = typeof(WebCrawlerService).GetMethod("TruncateUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [url, maxLen])!;
    }

    #region Blocked Extensions

    [Theory]
    [InlineData("https://example.com/file.pdf", true)]
    [InlineData("https://example.com/image.png", true)]
    [InlineData("https://example.com/image.jpg", true)]
    [InlineData("https://example.com/image.jpeg", true)]
    [InlineData("https://example.com/image.gif", true)]
    [InlineData("https://example.com/image.svg", true)]
    [InlineData("https://example.com/image.webp", true)]
    [InlineData("https://example.com/archive.zip", true)]
    [InlineData("https://example.com/style.css", true)]
    [InlineData("https://example.com/script.js", true)]
    [InlineData("https://example.com/data.json", true)]
    [InlineData("https://example.com/feed.rss", true)]
    [InlineData("https://example.com/feed.atom", true)]
    [InlineData("https://example.com/video.mp4", true)]
    [InlineData("https://example.com/page.html", false)]
    [InlineData("https://example.com/article", false)]
    [InlineData("https://example.com/docs/getting-started", false)]
    public void IsBlockedExtension_CorrectlyFilters(string url, bool expected)
    {
        IsBlockedExtension(url).Should().Be(expected);
    }

    #endregion

    #region URL Normalization

    [Fact]
    public void NormalizeUrl_RemovesFragment()
    {
        var result = NormalizeUrl("https://example.com/page#section");
        result.Should().Be("https://example.com/page");
    }

    [Fact]
    public void NormalizeUrl_RemovesTrailingSlash()
    {
        var result = NormalizeUrl("https://example.com/page/");
        result.Should().Be("https://example.com/page");
    }

    [Fact]
    public void NormalizeUrl_LowercasesHostPreservesPathCase()
    {
        var result = NormalizeUrl("https://Example.COM/Page");
        // Host is lowercased (per HTTP spec), path case is preserved
        // (raw.githubusercontent.com is case-sensitive)
        result.Should().Be("https://example.com/Page");
    }

    [Fact]
    public void NormalizeUrl_PreservesPath()
    {
        var result = NormalizeUrl("https://docs.example.com/api/v2/reference");
        result.Should().Be("https://docs.example.com/api/v2/reference");
    }

    [Fact]
    public void NormalizeUrl_HandlesRootUrl()
    {
        var result = NormalizeUrl("https://example.com/");
        result.Should().Be("https://example.com");
    }

    #endregion

    #region URL Truncation

    [Fact]
    public void TruncateUrl_ShortUrlUnchanged()
    {
        TruncateUrl("https://example.com", 60).Should().Be("https://example.com");
    }

    [Fact]
    public void TruncateUrl_LongUrlTruncated()
    {
        var longUrl = "https://example.com/" + new string('a', 100);
        var result = TruncateUrl(longUrl, 60);
        result.Length.Should().Be(60);
        result.Should().EndWith("...");
    }

    #endregion

    #region GitHub Raw Mode

    [Theory]
    [InlineData("https://github.com/scottgal/mostlylucid.nugetpackages/blob/main/README.md",
        "https://raw.githubusercontent.com/scottgal/mostlylucid.nugetpackages/main/README.md")]
    [InlineData("https://github.com/owner/repo/blob/develop/docs/guide.md",
        "https://raw.githubusercontent.com/owner/repo/develop/docs/guide.md")]
    [InlineData("https://github.com/owner/repo/tree/main/src",
        "https://raw.githubusercontent.com/owner/repo/main/src")]
    public void ConvertToRawGitHubUrl_ConvertsCorrectly(string input, string expected)
    {
        WebCrawlerService.ConvertToRawGitHubUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo")] // no blob/tree segment
    [InlineData("https://example.com/page")] // not GitHub
    [InlineData("not-a-url")] // invalid
    public void ConvertToRawGitHubUrl_ReturnsNullForInvalid(string input)
    {
        WebCrawlerService.ConvertToRawGitHubUrl(input).Should().BeNull();
    }

    [Theory]
    [InlineData("docs/readme.md", true)]
    [InlineData("guide.markdown", true)]
    [InlineData("intro.mdx", true)]
    [InlineData("README.MD", true)]
    [InlineData("page.html", false)]
    [InlineData("script.js", false)]
    [InlineData("readme.md#section", true)] // fragment stripped before check
    [InlineData("readme.md?v=2", true)] // query stripped before check
    public void IsMarkdownUrl_CorrectlyIdentifies(string url, bool expected)
    {
        var method = typeof(WebCrawlerService).GetMethod("IsMarkdownUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        ((bool)method.Invoke(null, [url])!).Should().Be(expected);
    }

    [Fact]
    public void ExtractMarkdownTitle_FindsFirstHeading()
    {
        var method = typeof(WebCrawlerService).GetMethod("ExtractMarkdownTitle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var md = "Some preamble\n# My Title\n\nContent here";
        ((string)method.Invoke(null, [md, "https://example.com/file.md"])!)
            .Should().Be("My Title");
    }

    [Fact]
    public void ExtractMarkdownTitle_FallsBackToFilename()
    {
        var method = typeof(WebCrawlerService).GetMethod("ExtractMarkdownTitle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var md = "No heading here, just content.";
        ((string)method.Invoke(null, [md, "https://example.com/my-guide.md"])!)
            .Should().Be("my-guide");
    }

    #endregion

    #region CrawlConfig

    [Fact]
    public void CrawlConfig_DefaultValues()
    {
        var config = new CrawlConfig();
        config.Name.Should().Be("default");
        config.MaxDepth.Should().Be(3);
        config.MaxPages.Should().Be(200);
        config.DelayMs.Should().Be(500);
        config.MaxConcurrency.Should().Be(3);
        config.TimeoutSeconds.Should().Be(15);
    }

    [Fact]
    public void CrawlConfig_CustomValues()
    {
        var config = new CrawlConfig
        {
            Name = "intranet",
            MaxDepth = 5,
            MaxPages = 1000,
            DelayMs = 250,
            MaxConcurrency = 5,
            TimeoutSeconds = 30
        };

        config.Name.Should().Be("intranet");
        config.MaxDepth.Should().Be(5);
        config.MaxPages.Should().Be(1000);
    }

    #endregion
}
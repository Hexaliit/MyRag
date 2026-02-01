using DoomSummarizer.Plugins.Subtitles;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.Plugins;

public class SubtitleDocumentHandlerTests
{
    private readonly SubtitleDocumentHandler _handler = new();

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Subtitles", fileName);

    [Fact]
    public void CanHandle_SrtFile_ReturnsTrue()
    {
        _handler.CanHandle("video.srt").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_VttFile_ReturnsTrue()
    {
        _handler.CanHandle("video.vtt").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_AssFile_ReturnsTrue()
    {
        _handler.CanHandle("video.ass").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_UnsupportedFile_ReturnsFalse()
    {
        _handler.CanHandle("video.mp4").Should().BeFalse();
        _handler.CanHandle("document.pdf").Should().BeFalse();
        _handler.CanHandle("readme.md").Should().BeFalse();
    }

    [Fact]
    public void SupportedExtensions_ContainsExpected()
    {
        _handler.SupportedExtensions.Should().Contain(".srt");
        _handler.SupportedExtensions.Should().Contain(".vtt");
        _handler.SupportedExtensions.Should().Contain(".ass");
        _handler.SupportedExtensions.Should().Contain(".ssa");
    }

    [Fact]
    public async Task ProcessAsync_SimpleSrt_ReturnsMarkdownWithTitle()
    {
        var path = TestDataPath("simple.srt");
        if (!File.Exists(path)) return; // Skip if test data not available

        var options = new DocumentHandlerOptions();
        var result = await _handler.ProcessAsync(path, options);

        result.Should().NotBeNull();
        result.Markdown.Should().NotBeNullOrEmpty();
        result.Title.Should().Be("simple");
        result.ContentType.Should().Be("subtitles");
    }

    [Fact]
    public async Task ProcessAsync_SimpleSrt_ContainsSubtitleText()
    {
        var path = TestDataPath("simple.srt");
        if (!File.Exists(path)) return;

        var options = new DocumentHandlerOptions();
        var result = await _handler.ProcessAsync(path, options);

        result.Markdown.Should().Contain("Hello and welcome to this video");
        result.Markdown.Should().Contain("programming");
        result.Markdown.Should().Contain("C#");
    }

    [Fact]
    public async Task ProcessAsync_SimpleSrt_IncludesMetadata()
    {
        var path = TestDataPath("simple.srt");
        if (!File.Exists(path)) return;

        var options = new DocumentHandlerOptions();
        var result = await _handler.ProcessAsync(path, options);

        result.Metadata.Should().NotBeNull();
        result.Metadata.Should().ContainKey("entry_count");
        result.Metadata.Should().ContainKey("format");
        result.Metadata["format"].Should().Be("SRT");
    }

    [Fact]
    public async Task ProcessAsync_ChaptersSrt_DetectsChapters()
    {
        var path = TestDataPath("chapters.srt");
        if (!File.Exists(path)) return;

        var options = new DocumentHandlerOptions();
        var result = await _handler.ProcessAsync(path, options);

        result.Metadata.Should().ContainKey("chapter_count");
        var chapterCount = (int)result.Metadata["chapter_count"];
        chapterCount.Should().BeGreaterThan(1, "chapters.srt has clear chapter markers and gaps");
    }

    [Fact]
    public async Task ProcessAsync_ChaptersSrt_MarkdownHasChapterHeadings()
    {
        var path = TestDataPath("chapters.srt");
        if (!File.Exists(path)) return;

        var options = new DocumentHandlerOptions();
        var result = await _handler.ProcessAsync(path, options);

        // Should contain ## headings for chapters
        result.Markdown.Should().Contain("##");
        result.Markdown.Should().StartWith("# chapters");
    }

    [Fact]
    public async Task ProcessAsync_VttFile_StripsHtmlTags()
    {
        var path = TestDataPath("simple.vtt");
        if (!File.Exists(path)) return;

        var options = new DocumentHandlerOptions();
        var result = await _handler.ProcessAsync(path, options);

        // HTML tags like <b> and <i> should be stripped
        result.Markdown.Should().NotContain("<b>");
        result.Markdown.Should().NotContain("</b>");
        result.Markdown.Should().NotContain("<i>");
        result.Markdown.Should().NotContain("</i>");
        // But the text content should remain
        result.Markdown.Should().Contain("welcome");
        result.Markdown.Should().Contain("basics");
    }
}

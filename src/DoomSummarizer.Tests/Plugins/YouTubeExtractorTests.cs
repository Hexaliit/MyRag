using DoomSummarizer.Sources.YouTube;

namespace DoomSummarizer.Tests.Plugins;

public class YouTubeExtractorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", true)]
    [InlineData("https://example.com/page", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void IsYouTubeUrl_DetectsCorrectly(string url, bool expected)
    {
        YouTubeExtractor.IsYouTubeUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void ExtractVideoId_ReturnsCorrectId(string url, string expectedId)
    {
        YouTubeExtractor.ExtractVideoId(url).Should().Be(expectedId);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void ExtractVideoId_InvalidUrl_ReturnsNull(string url)
    {
        YouTubeExtractor.ExtractVideoId(url).Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_InvalidUrl_ThrowsArgumentException()
    {
        var extractor = new YouTubeExtractor();

        var act = () => extractor.ExtractAsync("https://example.com/not-youtube");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Not a valid YouTube URL*");
    }

    [Fact]
    public void TranscriptSegmentInfo_DefaultConfidence_IsZero()
    {
        var segment = new TranscriptSegmentInfo(0.0, 5.0, "Hello");

        segment.Confidence.Should().Be(0.0);
    }

    [Fact]
    public void TranscriptSegmentInfo_WithConfidence_StoresValue()
    {
        var segment = new TranscriptSegmentInfo(0.0, 5.0, "Hello", 0.95);

        segment.Confidence.Should().Be(0.95);
    }

    [Fact]
    public void YouTubeExtractionResult_StoresAllFields()
    {
        var items = new List<DoomSummarizer.Models.ContentItem>();
        var result = new YouTubeExtractionResult(
            "dQw4w9WgXcQ",
            "Test Video",
            "Test Author",
            "Test Channel",
            TimeSpan.FromMinutes(5),
            "A test description",
            "https://img.youtube.com/thumb.jpg",
            items);

        result.VideoId.Should().Be("dQw4w9WgXcQ");
        result.Title.Should().Be("Test Video");
        result.Author.Should().Be("Test Author");
        result.Channel.Should().Be("Test Channel");
        result.Duration.Should().Be(TimeSpan.FromMinutes(5));
        result.Description.Should().Be("A test description");
        result.ThumbnailUrl.Should().Be("https://img.youtube.com/thumb.jpg");
        result.Items.Should().BeSameAs(items);
    }
}

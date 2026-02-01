using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Plugins.Subtitles;

namespace DoomSummarizer.Tests.Plugins;

public class SubtitlesProcessorPluginTests
{
    private readonly SubtitlesProcessorPlugin _plugin = new();

    [Fact]
    public void Metadata_HasCorrectName()
    {
        _plugin.Metadata.Name.Should().Be("subtitles");
    }

    [Fact]
    public void Metadata_SupportsExpectedExtensions()
    {
        _plugin.Metadata.SupportedExtensions.Should().Contain(".srt");
        _plugin.Metadata.SupportedExtensions.Should().Contain(".vtt");
        _plugin.Metadata.SupportedExtensions.Should().Contain(".ass");
        _plugin.Metadata.SupportedExtensions.Should().Contain(".ssa");
    }

    [Fact]
    public void Metadata_SupportsExpectedDocumentTypes()
    {
        _plugin.Metadata.DocumentTypes.Should().Contain("subtitles");
        _plugin.Metadata.DocumentTypes.Should().Contain("transcript");
    }

    [Fact]
    public void CanProcess_SrtExtension_ReturnsTrue()
    {
        var context = new ProcessingContext { FileName = "test.srt" };

        _plugin.CanProcess("some content", context).Should().BeTrue();
    }

    [Fact]
    public void CanProcess_VttExtension_ReturnsTrue()
    {
        var context = new ProcessingContext { FileName = "test.vtt" };

        _plugin.CanProcess("some content", context).Should().BeTrue();
    }

    [Fact]
    public void CanProcess_SubtitlesDocType_ReturnsTrue()
    {
        var context = new ProcessingContext { DocumentType = "subtitles" };

        _plugin.CanProcess("some content", context).Should().BeTrue();
    }

    [Fact]
    public void CanProcess_TranscriptDocType_ReturnsTrue()
    {
        var context = new ProcessingContext { DocumentType = "transcript" };

        _plugin.CanProcess("some content", context).Should().BeTrue();
    }

    [Fact]
    public void CanProcess_UnrelatedFile_ReturnsFalse()
    {
        var context = new ProcessingContext { FileName = "document.pdf" };

        _plugin.CanProcess("some content", context).Should().BeFalse();
    }

    [Fact]
    public void CanProcess_NoContext_ReturnsFalse()
    {
        var context = new ProcessingContext();

        _plugin.CanProcess("some content", context).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessAsync_WithChapters_BuildsDocumentTree()
    {
        var markdown = """
            # Test Video

            ## [0:00] Introduction
            Welcome to the video.

            ## [2:30] Setup
            Let's set up the project.

            ## [5:15] Demo
            Here is the demonstration.
            """;

        var options = new ProcessorOptions();
        var result = await _plugin.ProcessAsync(markdown, options);

        result.DocumentTree.Should().NotBeNull();
        result.DocumentTree.Level.Should().Be(0);
        result.DocumentTree.Children.Should().HaveCount(3);
        result.DocumentType.Should().Be("subtitles");
    }

    [Fact]
    public async Task ProcessAsync_WithChapters_SetsCorrectSequences()
    {
        var markdown = """
            # Video

            ## [0:00] Part One
            Content one.

            ## [5:00] Part Two
            Content two.
            """;

        var options = new ProcessorOptions();
        var result = await _plugin.ProcessAsync(markdown, options);

        result.DocumentTree.Children[0].Sequence.Should().Be(0);
        result.DocumentTree.Children[1].Sequence.Should().Be(1);
        result.DocumentTree.Children[0].LevelLabel.Should().Be("chapter");
    }

    [Fact]
    public async Task ProcessAsync_NoChapters_CreatesSingleNode()
    {
        var markdown = "Just plain text content with no headings at all.";

        var options = new ProcessorOptions();
        var result = await _plugin.ProcessAsync(markdown, options);

        result.DocumentTree.Children.Should().HaveCount(1);
        result.DocumentTree.Children[0].LevelLabel.Should().Be("section");
        result.DocumentTree.Children[0].Content.Should().Contain("plain text");
    }

    [Fact]
    public async Task ProcessAsync_WithFilePath_UsesFileNameAsTitle()
    {
        var markdown = "Some content";
        var options = new ProcessorOptions { FilePath = @"C:\videos\my-lecture.srt" };

        var result = await _plugin.ProcessAsync(markdown, options);

        result.DocumentTree.Title.Should().Be("my-lecture");
    }

    [Fact]
    public async Task ProcessAsync_NoFilePath_DefaultsToSubtitles()
    {
        var markdown = "Some content";
        var options = new ProcessorOptions();

        var result = await _plugin.ProcessAsync(markdown, options);

        result.DocumentTree.Title.Should().Be("Subtitles");
    }

    [Fact]
    public void DocumentHandler_IsSubtitleDocumentHandler()
    {
        _plugin.DocumentHandler.Should().BeOfType<SubtitleDocumentHandler>();
    }
}

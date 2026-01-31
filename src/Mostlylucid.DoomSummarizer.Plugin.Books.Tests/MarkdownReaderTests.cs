using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.Summarizers.Reader.Markdown;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Tests;

public class MarkdownReaderTests
{
    private readonly MarkdownReader _reader = new(NullLogger<MarkdownReader>.Instance);
    [Fact]
    public async Task ReadAsync_FrontMatter_ExtractsTitleAndMetadata()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                ---
                title: My Test Post
                author: Scott Galloway
                date: 2024-01-15
                ---

                # Introduction

                This is the introduction to the test document.

                ## Section 1

                Content of section 1.
                """);

            var result = await _reader.ReadAsync(tempFile);

            result.Title.Should().Be("My Test Post");
            result.Metadata.Should().ContainKey("author");
            result.Metadata["author"].Should().Be("Scott Galloway");
            result.Markdown.Should().NotContain("---");
            result.Markdown.Should().Contain("Introduction");
            result.WordCount.Should().BeGreaterThan(5);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task ReadAsync_NoFrontMatter_UsesTitleFromHeading()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                # My Document Title

                Some content here.
                """);

            var result = await _reader.ReadAsync(tempFile);

            result.Title.Should().Be("My Document Title");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task ReadAsync_NoHeading_UsesFilenameAsTitle()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "my-document.md");
        try
        {
            await File.WriteAllTextAsync(tempFile, "Just some text without any heading.");

            var result = await _reader.ReadAsync(tempFile);

            result.Title.Should().Be("my-document");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task ReadAsync_PlainTxtFile_ReadsSuccessfully()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, "This is a plain text file with some content.");

            var result = await _reader.ReadAsync(tempFile);

            result.Markdown.Should().NotBeNullOrWhiteSpace();
            result.WordCount.Should().BeGreaterThan(0);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}

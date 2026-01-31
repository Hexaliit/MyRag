using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.Summarizers.Reader.Gutenberg;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Tests;

public class GutenbergReaderTests
{
    private readonly GutenbergReader _reader = new(NullLogger<GutenbergReader>.Instance);
    private static readonly string SampleDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid.DocSummarizer", "sampledata"));

    [Fact]
    public void IsGutenbergText_WithGutenbergHeader_ReturnsTrue()
    {
        var text = """
            The Project Gutenberg eBook of Frankenstein

            Title: Frankenstein; Or, The Modern Prometheus
            Author: Mary Wollstonecraft Shelley

            *** START OF THE PROJECT GUTENBERG EBOOK FRANKENSTEIN ***

            Letter 1
            """;

        GutenbergReader.IsGutenbergText(text).Should().BeTrue();
    }

    [Fact]
    public void IsGutenbergText_WithNormalText_ReturnsFalse()
    {
        GutenbergReader.IsGutenbergText("Hello world, this is just a normal text file.")
            .Should().BeFalse();
    }

    [Fact]
    public void ExtractMetadata_ParsesTitleAndAuthor()
    {
        var text = """
            The Project Gutenberg eBook

            Title: Pride and Prejudice
            Author: Jane Austen
            Release Date: June 1, 1998 [eBook #1342]
            Language: English

            *** START OF THE PROJECT GUTENBERG EBOOK ***

            Content here.
            """;

        var metadata = GutenbergReader.ExtractMetadata(text);

        metadata.Should().ContainKey("title");
        metadata["title"].Should().Be("Pride and Prejudice");
        metadata.Should().ContainKey("author");
        metadata["author"].Should().Be("Jane Austen");
        metadata.Should().ContainKey("language");
        metadata["language"].Should().Be("English");
        metadata.Should().ContainKey("ebook_number");
        metadata["ebook_number"].Should().Be("1342");
    }

    [Fact]
    public void StripBoilerplate_RemovesHeaderAndFooter()
    {
        var text = """
            The Project Gutenberg eBook of Something

            Title: Something
            Author: Someone

            *** START OF THE PROJECT GUTENBERG EBOOK SOMETHING ***

            This is the actual content of the book.

            THE END

            *** END OF THE PROJECT GUTENBERG EBOOK SOMETHING ***

            Project Gutenberg footer stuff here.
            """;

        var stripped = GutenbergReader.StripBoilerplate(text);

        stripped.Should().Contain("actual content of the book");
        stripped.Should().NotContain("Project Gutenberg eBook");
        stripped.Should().NotContain("footer stuff");
    }

    [Fact]
    public async Task ReadAsync_RealGutenbergZip_ExtractsContent()
    {
        var path = Path.Combine(SampleDataDir, "pg46-h.zip");
        if (!File.Exists(path)) return;

        var result = await _reader.ReadAsync(path);

        result.Markdown.Should().NotBeNullOrWhiteSpace();
        result.WordCount.Should().BeGreaterThan(1000);
        result.Title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReadAsync_RealGutenbergZip24264_ExtractsContent()
    {
        var path = Path.Combine(SampleDataDir, "pg24264-h.zip");
        if (!File.Exists(path)) return;

        var result = await _reader.ReadAsync(path);

        result.Markdown.Should().NotBeNullOrWhiteSpace();
        result.WordCount.Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task ReadAsync_RealGutenbergTxt_ExtractsAndStrips()
    {
        var path = Path.Combine(SampleDataDir, "pg84.txt");
        if (!File.Exists(path)) return;

        var result = await _reader.ReadAsync(path);

        result.Markdown.Should().NotBeNullOrWhiteSpace();
        result.WordCount.Should().BeGreaterThan(50_000,
            because: "Frankenstein is a substantial novel");
        result.Metadata.Should().ContainKey("title");
    }
}

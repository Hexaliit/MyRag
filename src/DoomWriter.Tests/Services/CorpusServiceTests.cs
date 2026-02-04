using System.Reflection;
using DoomWriter.Services;

namespace DoomWriter.Tests.Services;

/// <summary>
///     Tests for CorpusService pure/stateless methods:
///     frontmatter parsing, segment extraction, hash computation.
///     Uses reflection to test private static helpers.
/// </summary>
public class CorpusServiceTests
{
    // --- Frontmatter parsing ---
    // We test via reflection since ParseFrontmatter is private static

    [Fact]
    public void ParseFrontmatter_ExtractsTitleAndSlug()
    {
        var content = """
                      ---
                      title: "My Great Article"
                      slug: my-great-article
                      date: 2025-01-15
                      ---

                      # Article Content

                      This is the body.
                      """;

        var (metadata, body) = InvokeParseFrontmatter(content);

        metadata.Should().ContainKey("title");
        metadata["title"].Should().Be("My Great Article");
        metadata.Should().ContainKey("slug");
        metadata["slug"].Should().Be("my-great-article");
        body.Should().Contain("Article Content");
        body.Should().NotContain("---");
    }

    [Fact]
    public void ParseFrontmatter_NoFrontmatter_ReturnsFullContent()
    {
        var content = "# Just a heading\n\nBody text.";

        var (metadata, body) = InvokeParseFrontmatter(content);

        metadata.Should().BeEmpty();
        body.Should().Be(content);
    }

    [Fact]
    public void ParseFrontmatter_EmptyFrontmatter_ReturnsBody()
    {
        var content = "---\n---\n\nBody after empty frontmatter.";

        var (metadata, body) = InvokeParseFrontmatter(content);

        metadata.Should().BeEmpty();
        body.Should().Contain("Body after empty frontmatter");
    }

    [Fact]
    public void ParseFrontmatter_StripsQuotes()
    {
        var content = "---\ntitle: \"Quoted Title\"\nauthor: 'Single Quoted'\n---\n\nBody.";

        var (metadata, _) = InvokeParseFrontmatter(content);

        metadata["title"].Should().Be("Quoted Title");
        metadata["author"].Should().Be("Single Quoted");
    }

    [Fact]
    public void ParseFrontmatter_HandlesColonInValue()
    {
        var content = "---\ntitle: Time: A History\n---\n\nBody.";

        var (metadata, _) = InvokeParseFrontmatter(content);

        metadata["title"].Should().Be("Time: A History");
    }

    // --- Segment extraction ---

    [Fact]
    public void ExtractSegments_SplitsByBlankLines()
    {
        // Segments must be >= 20 chars to pass the minimum length filter
        var markdown =
            "First paragraph with enough content here.\n\nSecond paragraph also has sufficient text.\n\nThird paragraph rounds out the document nicely.";

        var segments = InvokeExtractSegments(markdown);

        segments.Should().HaveCount(3);
    }

    [Fact]
    public void ExtractSegments_SkipsHeadings()
    {
        var markdown =
            "# Heading\n\nParagraph content that is long enough to pass the filter.\n\n## Another heading\n\nMore content that also has enough text for extraction.";

        var segments = InvokeExtractSegments(markdown);

        segments.Should().HaveCount(2);
        segments.Should().OnlyContain(s => !s.Text.StartsWith('#'));
    }

    [Fact]
    public void ExtractSegments_SkipsShortSegments()
    {
        var markdown = "OK\n\nThis is a paragraph with enough text to be a real segment.";

        var segments = InvokeExtractSegments(markdown);

        // "OK" is <20 chars, should be skipped
        segments.Should().ContainSingle();
        segments[0].Text.Should().Contain("enough text");
    }

    [Fact]
    public void ExtractSegments_StripsMarkdownFormatting()
    {
        var markdown = "Normal.\n\n**Bold** and *italic* and `code` text here for testing.";

        var segments = InvokeExtractSegments(markdown);

        var longSegment = segments.FirstOrDefault(s => s.Text.Contains("Bold"));
        longSegment.Should().NotBeNull();
        longSegment!.Text.Should().NotContain("**");
        longSegment.Text.Should().NotContain("*");
        longSegment.Text.Should().NotContain("`");
    }

    // --- Hash computation ---

    [Fact]
    public void ComputeHash_SameContent_SameHash()
    {
        var hash1 = InvokeComputeHash("test content");
        var hash2 = InvokeComputeHash("test content");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHash_DifferentContent_DifferentHash()
    {
        var hash1 = InvokeComputeHash("content A");
        var hash2 = InvokeComputeHash("content B");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHash_ProducesHexString()
    {
        var hash = InvokeComputeHash("test");

        hash.Should().NotBeNullOrEmpty();
        // XxHash64 produces 8 bytes = 16 hex chars
        hash.Should().MatchRegex("^[0-9A-F]+$");
    }

    // --- Reflection helpers to invoke private static methods ---

    private static (Dictionary<string, string> metadata, string body) InvokeParseFrontmatter(string content)
    {
        var method = typeof(CorpusService).GetMethod("ParseFrontmatter",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("ParseFrontmatter should exist as private static");

        var result = method!.Invoke(null, [content]);
        var tuple = ((Dictionary<string, string>, string))result!;
        return tuple;
    }

    private static List<CorpusSegment> InvokeExtractSegments(string markdown)
    {
        var method = typeof(CorpusService).GetMethod("ExtractSegments",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("ExtractSegments should exist as private static");

        return (List<CorpusSegment>)method!.Invoke(null, [markdown])!;
    }

    private static string InvokeComputeHash(string content)
    {
        var method = typeof(CorpusService).GetMethod("ComputeHash",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("ComputeHash should exist as private static");

        return (string)method!.Invoke(null, [content])!;
    }
}
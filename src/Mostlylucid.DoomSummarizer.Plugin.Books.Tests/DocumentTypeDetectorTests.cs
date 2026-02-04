using DoomSummarizer.Services;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Tests;

public class DocumentTypeDetectorTests
{
    [Fact]
    public void FindSubstantialChunk_SkipsCoverPage()
    {
        // Simulates a DOCX converted to markdown with cover page + publisher info
        var content = """
                      The Sign of the Four

                      Arthur Conan Doyle

                      Published by Lippincott's Monthly Magazine, 1890

                      Copyright 1890 by J.B. Lippincott Company
                      All Rights Reserved
                      First Edition

                      ISBN 978-0-123456-78-9

                      Table of Contents

                      Chapter I. The Science of Deduction
                      Chapter II. The Statement of the Case

                      ---

                      In the year 1888 I purchased a copy of the morning paper and found therein an
                      advertisement which was to have a singular effect upon my life. It ran as follows:
                      "To be let, at a very reasonable rent, the Baker Street lodgings," and went on to
                      describe their advantages at some length. That same afternoon I found myself at the
                      well-known address, and was shown the rooms by a most obliging landlady.

                      Sherlock Holmes was pacing the room with long, eager strides, his thin face flushed
                      and his eyes bright with the hard, dry, metallic glitter which spoke of intense
                      concentration upon whatever matter was occupying his remarkable mind.
                      """;

        var chunk = DocumentTypeDetector.FindSubstantialChunk(content);

        // Should skip the cover page and start at prose
        chunk.Should().NotContain("Table of Contents");
        chunk.Should().NotContain("Copyright");
        chunk.Should().NotContain("ISBN");
        chunk.Should().Contain("1888");
    }

    [Fact]
    public void FindSubstantialChunk_SkipsGutenbergBoilerplate()
    {
        var content = """
                      *** START OF THE PROJECT GUTENBERG EBOOK FRANKENSTEIN ***

                      Produced by Judith Boss. HTML version by Al Haines.

                      This eBook is for the use of anyone anywhere in the United States and
                      most other parts of the world at no cost and with almost no restrictions
                      whatsoever. You may copy it, give it away or re-use it under the terms
                      of the Project Gutenberg License included with this eBook.

                      FRANKENSTEIN

                      by Mary Wollstonecraft Shelley

                      Letter 1

                      To Mrs. Saville, England.

                      St. Petersburgh, Dec. 11th, 17—

                      You will rejoice to hear that no disaster has accompanied the commencement of an
                      enterprise which you have regarded with such evil forebodings. I arrived here
                      yesterday, and my first task is to assure my dear sister of my welfare and
                      increasing confidence in the success of my undertaking.
                      """;

        var chunk = DocumentTypeDetector.FindSubstantialChunk(content);

        chunk.Should().NotContain("PROJECT GUTENBERG");
        chunk.Should().NotContain("eBook is for the use");
        // Should find the actual story text
        chunk.Should().Contain("rejoice");
    }

    [Fact]
    public void FindSubstantialChunk_HandlesPureProseDocument()
    {
        // A document that starts immediately with prose (no front matter)
        var content = """
                      It was the best of times, it was the worst of times, it was the age of wisdom,
                      it was the age of foolishness, it was the epoch of belief, it was the epoch of
                      incredulity, it was the season of Light, it was the season of Darkness, it was
                      the spring of hope, it was the winter of despair.

                      We had everything before us, we had nothing before us, we were all going direct
                      to Heaven, we were all going direct the other way—in short, the period was so
                      far like the present period that some of its noisiest authorities insisted on
                      its being received, for good or for evil, in the superlative degree of comparison only.
                      """;

        var chunk = DocumentTypeDetector.FindSubstantialChunk(content);

        // Should start at the very beginning since it's all prose
        chunk.Should().Contain("best of times");
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsHeuristicWhenConfidenceHigh()
    {
        // No OllamaService — pure heuristic path
        var detector = new DocumentTypeDetector(null);

        var result = await detector.ClassifyAsync(
            "some content",
            "fiction",
            0.80,
            ["chapter_markers→fiction", "dialogue→fiction"]);

        result.Type.Should().Be("fiction");
        result.Confidence.Should().Be(0.80);
        result.Source.Should().Be("heuristic");
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsHeuristicWhenNoOllama()
    {
        var detector = new DocumentTypeDetector(null);

        var result = await detector.ClassifyAsync(
            "some content",
            "technical",
            0.20);

        // Even with low confidence, no Ollama means heuristic only
        result.Type.Should().Be("technical");
        result.Source.Should().Be("heuristic");
    }

    [Fact]
    public async Task ClassifyAsync_ThresholdIsRespected()
    {
        var detector = new DocumentTypeDetector(null) { LlmFallbackThreshold = 0.60 };

        // At threshold — should still return heuristic
        var result = await detector.ClassifyAsync(
            "content", "fiction", 0.60);

        result.Source.Should().Be("heuristic");

        // Below threshold but no ollama — heuristic anyway
        var result2 = await detector.ClassifyAsync(
            "content", "fiction", 0.59);

        result2.Source.Should().Be("heuristic");
    }

    [Fact]
    public void FindSubstantialChunk_FallsBackForShortContent()
    {
        // Very short content with no clear prose blocks
        var content = "Title\nAuthor\nShort.";

        var chunk = DocumentTypeDetector.FindSubstantialChunk(content, 500);

        // Should return something, not throw
        chunk.Should().NotBeEmpty();
    }

    [Fact]
    public void FindSubstantialChunk_HandlesEmptyContent()
    {
        DocumentTypeDetector.FindSubstantialChunk("").Should().BeEmpty();
        DocumentTypeDetector.FindSubstantialChunk("   ").Should().Be("   ");
    }

    [Fact]
    public void FindSubstantialChunk_DoesNotFlagProseAboutEbooks()
    {
        // Content discussing ebooks should NOT be treated as front matter
        var content = """
                      The rise of the ebook market has transformed publishing in ways that were unimaginable
                      just two decades ago. Traditional publishers have had to adapt their business models to
                      accommodate digital distribution, while independent authors have found new opportunities
                      to reach readers directly through platforms like Amazon Kindle and Apple Books.

                      This shift has also raised important questions about digital rights management, pricing
                      strategies, and the future of physical bookstores in an increasingly digital world.
                      """;

        var chunk = DocumentTypeDetector.FindSubstantialChunk(content);

        // Should find this content as prose, not skip it as front matter
        chunk.Should().Contain("publishing");
    }
}
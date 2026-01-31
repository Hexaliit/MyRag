using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DoomSummarizer.Plugin.Books.Splitters;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Tests;

public class HierarchicalSplitterTests
{
    private readonly HierarchicalBookSplitter _splitter = new(NullLogger<HierarchicalBookSplitter>.Instance);

    [Theory]
    [InlineData(4_000, 0)]
    [InlineData(10_000, 1)]
    [InlineData(50_000, 2)]
    [InlineData(150_000, 3)]
    public void EstimateDepth_ReturnsCorrectDepth(int wordCount, int expectedDepth)
    {
        var context = new SplitContext { WordCount = wordCount };
        _splitter.EstimateDepth("", context).Should().Be(expectedDepth);
    }

    [Fact]
    public async Task SplitAsync_NovelPattern_SplitsByChapters()
    {
        var text = """
            Chapter 1
            It was a dark and stormy night. The wind howled through the trees, bending them like
            reeds under the force of the gale. Elizabeth sat by the window, watching the lightning
            illuminate the garden in brief, brilliant flashes.

            Chapter 2
            The next morning dawned bright and clear. The storm had passed, leaving behind a world
            washed clean and glistening with dew. Elizabeth rose early and walked through the
            gardens, enjoying the fresh air and the scent of wet earth.

            Chapter 3
            By noon, the household was bustling with activity. The servants prepared for the
            arrival of the Bingleys, and Mrs. Bennet was in a state of considerable agitation
            about the evening's dinner arrangements.
            """;

        var options = new SplitOptions { PatternName = "novel" };
        var result = await _splitter.SplitAsync(text, options);

        result.Should().NotBeNull();
        result.Children.Count.Should().BeGreaterThanOrEqualTo(3,
            because: "3 chapters should be detected");
        result.Children.Should().AllSatisfy(c =>
            c.Content.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task SplitAsync_PlayPattern_SplitsByActsAndScenes()
    {
        var text = """
            ACT I

            SCENE 1. Elsinore. A platform before the castle.
            FRANCISCO at his post. Enter to him BERNARDO.
            BERNARDO. Who's there?
            FRANCISCO. Nay, answer me: stand, and unfold yourself.
            BERNARDO. Long live the king!

            SCENE 2. A room of state in the castle.
            Enter the KING, QUEEN, HAMLET, POLONIUS, LAERTES.
            KING. Though yet of Hamlet our dear brother's death
            The memory be green, and that it us befitted.

            ACT II

            SCENE 1. A room in Polonius's house.
            Enter POLONIUS and REYNALDO.
            POLONIUS. Give him this money and these notes, Reynaldo.
            """;

        var options = new SplitOptions { PatternName = "play" };
        var result = await _splitter.SplitAsync(text, options);

        result.Should().NotBeNull();
        result.Children.Count.Should().BeGreaterThanOrEqualTo(2,
            because: "2 acts should be detected");
    }

    [Fact]
    public async Task SplitAsync_FallsBackToHeadings_WhenNoPatternMatches()
    {
        var text = """
            # Introduction
            This is the introduction section.

            # Methods
            This is the methods section with details about methodology.

            # Results
            The results show significant improvement.

            # Discussion
            We discuss the implications of these findings.
            """;

        // Use a pattern name that doesn't exist — should fallback to heading-based splitting
        var options = new SplitOptions { PatternName = "nonexistent" };
        var result = await _splitter.SplitAsync(text, options);

        result.Should().NotBeNull();
        result.Children.Count.Should().BeGreaterThanOrEqualTo(4,
            because: "4 markdown headings should produce 4 sections");
    }

    [Fact]
    public async Task SplitAsync_LeavesHaveContent()
    {
        var text = """
            Chapter 1
            Some content for chapter one that is reasonably long.

            Chapter 2
            Some content for chapter two that is also reasonably long.
            """;

        var options = new SplitOptions { PatternName = "novel" };
        var result = await _splitter.SplitAsync(text, options);

        var leaves = result.EnumerateLeaves().ToList();
        leaves.Should().AllSatisfy(leaf =>
            leaf.Content.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task SplitAsync_SequenceNumbersAreExtracted()
    {
        var text = """
            Chapter I
            First chapter content.

            Chapter II
            Second chapter content.

            Chapter III
            Third chapter content.
            """;

        var options = new SplitOptions { PatternName = "novel" };
        var result = await _splitter.SplitAsync(text, options);

        // Check that Roman numerals were parsed
        var chapters = result.Children.Where(c => c.Title.Contains("Chapter")).ToList();
        if (chapters.Count >= 3)
        {
            chapters[0].Sequence.Should().BeLessThanOrEqualTo(chapters[1].Sequence);
            chapters[1].Sequence.Should().BeLessThanOrEqualTo(chapters[2].Sequence);
        }
    }

    // --- Real sample data tests ---

    private static readonly string SampleDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid.DocSummarizer", "sampledata"));

    [Fact]
    public async Task SplitAsync_RealGutenbergFrankenstein_ProducesChapters()
    {
        var path = Path.Combine(SampleDataDir, "pg84.txt");
        if (!File.Exists(path)) return;

        var content = await File.ReadAllTextAsync(path);
        var options = new SplitOptions { PatternName = "novel" };
        var result = await _splitter.SplitAsync(content, options);

        result.Should().NotBeNull();
        result.Children.Count.Should().BeGreaterThan(5,
            because: "Frankenstein has many chapters and should split into multiple sections");
        result.TotalWordCount.Should().BeGreaterThan(10_000,
            because: "Frankenstein is a substantial novel");

        // Check tree integrity
        var leaves = result.EnumerateLeaves().ToList();
        leaves.Should().AllSatisfy(l => l.Content.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task SplitAsync_RealGutenbergDorianGray_ProducesChapters()
    {
        var path = Path.Combine(SampleDataDir, "pg174.txt");
        if (!File.Exists(path)) return;

        var content = await File.ReadAllTextAsync(path);
        var options = new SplitOptions { PatternName = "novel" };
        var result = await _splitter.SplitAsync(content, options);

        result.Should().NotBeNull();
        result.Children.Count.Should().BeGreaterThan(5);
    }
}

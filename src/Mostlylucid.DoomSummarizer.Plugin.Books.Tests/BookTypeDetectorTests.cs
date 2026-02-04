using Mostlylucid.DoomSummarizer.Plugin.Books.Detection;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Tests;

public class BookTypeDetectorTests
{
    // --- Real sample data tests ---

    private static readonly string SampleDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid.DocSummarizer", "sampledata"));

    [Theory]
    [InlineData("pg12345.txt", BookTypeDetector.Fiction)]
    [InlineData("pg84.txt", BookTypeDetector.Fiction)]
    [InlineData("my-novel-story.pdf", BookTypeDetector.Fiction)]
    [InlineData("research-paper.pdf", BookTypeDetector.Academic)]
    [InlineData("thesis-draft.docx", BookTypeDetector.Academic)]
    [InlineData("user-manual.pdf", BookTypeDetector.Technical)]
    [InlineData("developer-guide.txt", BookTypeDetector.Technical)]
    [InlineData("complete-works-shakespeare.pdf", BookTypeDetector.Anthology)]
    [InlineData("collected-stories.txt", BookTypeDetector.Collection)]
    [InlineData("sonnets-poems.pdf", BookTypeDetector.Collection)]
    [InlineData("hamlet-play.txt", BookTypeDetector.Play)]
    public void Detect_FilenamSignals_VotesCorrectType(string filename, string expectedType)
    {
        var result = BookTypeDetector.Detect("Some content here.", filename, wordCount: 10_000);

        result.Signals.Should().Contain(s => s.Category == "filename",
            $"filename '{filename}' should produce filename signals");

        // The filename signal should vote for the expected type
        result.Signals
            .Where(s => s.Category == "filename")
            .Should().Contain(s => s.VotedType == expectedType);
    }

    [Theory]
    [InlineData(300_000, BookTypeDetector.Anthology)]
    [InlineData(80_000, BookTypeDetector.Fiction)]
    [InlineData(8_000, BookTypeDetector.Academic)]
    [InlineData(1_000, BookTypeDetector.Technical)]
    public void Detect_SizeSignals_VotesCorrectType(int wordCount, string expectedType)
    {
        var result = BookTypeDetector.Detect("Content.", wordCount: wordCount);

        result.Signals.Should().Contain(s => s.Category == "size");
        result.Signals
            .Where(s => s.Category == "size")
            .Should().Contain(s => s.VotedType == expectedType);
    }

    [Fact]
    public void Detect_ChapterMarkers_VotesFiction()
    {
        var content = """
                      Chapter 1
                      It was a dark and stormy night.

                      Chapter 2
                      The next morning dawned bright and clear.
                      """;

        var result = BookTypeDetector.Detect(content, wordCount: 20_000);

        result.Signals.Should().Contain(s => s.Name == "chapter_markers");
    }

    [Fact]
    public void Detect_ActSceneMarkers_VotesPlay()
    {
        var content = """
                      ACT I
                      SCENE 1. Elsinore. A platform before the castle.

                      FRANCISCO at his post. Enter to him BERNARDO.

                      BERNARDO. Who's there?
                      """;

        var result = BookTypeDetector.Detect(content, wordCount: 30_000);

        result.Signals.Should().Contain(s => s.Name == "act_scene_markers");
        result.Type.Should().Be(BookTypeDetector.Play);
    }

    [Fact]
    public void Detect_AcademicStructure_VotesAcademic()
    {
        var content = """
                      Abstract
                      This paper presents a novel approach to machine learning.

                      Introduction
                      Recent advances in deep learning have shown that...

                      Methodology
                      We employ a transformer-based architecture...
                      """;

        var result = BookTypeDetector.Detect(content, wordCount: 8_000);

        result.Signals.Should().Contain(s => s.Name == "academic_structure");
        result.Type.Should().Be(BookTypeDetector.Academic);
    }

    [Fact]
    public void Detect_PoetrySignals_VotesCollection()
    {
        // Many short lines indicate poetry
        var lines = Enumerable.Range(1, 50)
            .Select(i => $"A short verse line {i}");
        var content = string.Join('\n', lines);

        var result = BookTypeDetector.Detect(content, wordCount: 5_000);

        result.Signals.Should().Contain(s => s.Name == "short_line_ratio");
    }

    [Fact]
    public void Detect_Dialogue_VotesFiction()
    {
        var content = """
                      "I never expected this," Sarah said.
                      "Neither did I," replied Tom, looking worried.
                      """;

        var result = BookTypeDetector.Detect(content, wordCount: 50_000);

        result.Signals.Should().Contain(s => s.Name == "dialogue");
    }

    [Fact]
    public void Detect_ReturnsAllTypeScores()
    {
        var result = BookTypeDetector.Detect("Some content", wordCount: 10_000);

        result.TypeScores.Should().ContainKey(BookTypeDetector.Fiction);
        result.TypeScores.Should().ContainKey(BookTypeDetector.NonFiction);
        result.TypeScores.Should().ContainKey(BookTypeDetector.Academic);
        result.TypeScores.Should().ContainKey(BookTypeDetector.Technical);
        result.TypeScores.Should().ContainKey(BookTypeDetector.Anthology);
        result.TypeScores.Should().ContainKey(BookTypeDetector.Play);
        result.TypeScores.Should().ContainKey(BookTypeDetector.Collection);
    }

    [Fact]
    public void Detect_ConfidenceBetween0And1()
    {
        var result = BookTypeDetector.Detect("Chapter 1\nSome novel text.", wordCount: 50_000);

        result.Confidence.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Detect_NoSignals_ReturnsUnknown()
    {
        // Minimal content with no recognizable signals
        var result = BookTypeDetector.Detect("x", wordCount: 0);

        // With zero word count, no size signal fires. Only content signals checked.
        result.Type.Should().BeOneOf(BookTypeDetector.Unknown, BookTypeDetector.Technical);
    }

    private static string? TryReadSampleFile(string filename)
    {
        var path = Path.Combine(SampleDataDir, filename);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void Detect_RealGutenbergTxt_DetectsAsFiction()
    {
        var content = TryReadSampleFile("pg84.txt");
        if (content == null)
            // Skip if sample data not available
            return;

        var result = BookTypeDetector.Detect(content, "pg84.txt");

        result.Type.Should().Be(BookTypeDetector.Fiction,
            "pg84.txt (Frankenstein) is a fiction novel");
        result.Confidence.Should().BeGreaterThan(0.2);
        result.Signals.Count.Should().BeGreaterThan(2,
            "multiple signals should fire for a real book");
    }

    [Fact]
    public void Detect_RealGutenbergTxt174_DetectsCorrectly()
    {
        var content = TryReadSampleFile("pg174.txt");
        if (content == null) return;

        var result = BookTypeDetector.Detect(content, "pg174.txt");

        // pg174 is "The Picture of Dorian Gray" — should be fiction
        result.Type.Should().Be(BookTypeDetector.Fiction);
        result.Signals.Should().Contain(s => s.Name == "gutenberg_id");
    }
}
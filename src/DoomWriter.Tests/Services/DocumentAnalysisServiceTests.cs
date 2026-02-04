using DoomSummarizer.Services;
using DoomWriter.Models;
using DoomWriter.Services;

namespace DoomWriter.Tests.Services;

public class DocumentAnalysisServiceTests
{
    private readonly DocumentAnalysisService _sut;

    public DocumentAnalysisServiceTests()
    {
        var settings = new WriterSettingsService();
        var nerService = new NerService(); // Falls back to regex when model not available
        _sut = new DocumentAnalysisService(settings, nerService);
    }

    // --- Heading extraction ---

    [Fact]
    public async Task AnalyzeAsync_ExtractsH1Heading()
    {
        var markdown = "# Hello World\n\nSome body text here.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700); // Allow debounce + analysis

        result.Should().NotBeNull();
        result!.Headings.Should().ContainSingle();
        result.Headings[0].Text.Should().Be("Hello World");
        result.Headings[0].Level.Should().Be(1);
        result.Headings[0].LineNumber.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ExtractsMultipleLevelHeadings()
    {
        var markdown = "# Title\n\nParagraph.\n\n## Section 1\n\nText.\n\n### Subsection\n\nMore text.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Headings.Should().HaveCount(3);
        result.Headings[0].Level.Should().Be(1);
        result.Headings[1].Level.Should().Be(2);
        result.Headings[2].Level.Should().Be(3);
        result.Headings[1].Text.Should().Be("Section 1");
    }

    [Fact]
    public async Task AnalyzeAsync_NoHeadingsInPlainText()
    {
        var markdown = "Just some plain text\nwith no headings at all.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Headings.Should().BeEmpty();
    }

    // --- Segment extraction ---

    [Fact]
    public async Task AnalyzeAsync_ExtractsSegmentsFromParagraphs()
    {
        var markdown = "# Title\n\nFirst paragraph with some text.\n\nSecond paragraph with more text.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Segments.Should().HaveCount(2);
        result.Segments[0].Text.Should().Contain("First paragraph");
        result.Segments[1].Text.Should().Contain("Second paragraph");
    }

    [Fact]
    public async Task AnalyzeAsync_SegmentsSortedByPosition()
    {
        var markdown = "# Title\n\nParagraph A.\n\nParagraph B.\n\nParagraph C.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Segments.Should().HaveCount(3);
        result.Segments[0].Position.Should().Be(0);
        result.Segments[1].Position.Should().Be(1);
        result.Segments[2].Position.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsHeadingsAsSegments()
    {
        var markdown = "# Title\n\n## Section\n\nActual content.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Segments.Should().ContainSingle();
        result.Segments[0].Text.Should().Be("Actual content.");
    }

    [Fact]
    public async Task AnalyzeAsync_SegmentSalienceReflectsVocabularyRichness()
    {
        var markdown =
            "# Title\n\nThe the the the the the the.\n\nDiverse varied rich complex nuanced articulate expressive prose writing content.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Segments.Should().HaveCount(2);
        // Segment with diverse vocabulary should have higher salience
        result.Segments[1].Salience.Should().BeGreaterThan(result.Segments[0].Salience);
    }

    // --- Entity extraction ---

    [Fact]
    public async Task AnalyzeAsync_ExtractsCapitalizedPhraseEntities()
    {
        var markdown = "# Title\n\nHacker News is a great site. Open Source Software is important.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Entities.Should().Contain(e => e.Name == "Hacker News");
        result.Entities.Should().Contain(e => e.Name == "Open Source Software");
    }

    [Fact]
    public async Task AnalyzeAsync_ExtractsBacktickEntities()
    {
        var markdown = "# Title\n\nThe `EmbeddingService` class handles vectors. Use `NerService` for NLP.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Entities.Should().Contain(e => e.Name == "EmbeddingService");
        result!.Entities.Should().Contain(e => e.Name == "NerService");
    }

    [Fact]
    public async Task AnalyzeAsync_EntityMentionCountTracksMultipleOccurrences()
    {
        var markdown = "# Title\n\nHacker News is popular.\n\nHacker News has millions of users.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        var hn = result!.Entities.FirstOrDefault(e => e.Name == "Hacker News");
        hn.Should().NotBeNull();
        hn!.MentionCount.Should().Be(2);
        hn.SectionIndices.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnalyzeAsync_FilterOutCommonPhrases()
    {
        var markdown = "# Title\n\nIn The beginning there was nothing. This Is Fine.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Entities.Should().NotContain(e => e.Name == "In The");
    }

    // --- Word count ---

    [Fact]
    public async Task AnalyzeAsync_CountsWordsCorrectly()
    {
        var markdown = "# Title\n\nOne two three four five.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        // "# Title\n\nOne two three four five." = "# + Title + One + two + three + four + five." = 7
        result!.WordCount.Should().Be(7);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyContentReturnsZeroWordCount()
    {
        var markdown = "";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.WordCount.Should().Be(0);
    }

    // --- Topic inference ---

    [Fact]
    public async Task AnalyzeAsync_InfersTopicsFromHeadings()
    {
        var markdown =
            "# Machine Learning\n\nNeural networks are powerful.\n\n## Natural Language Processing\n\nNLP is used for text analysis.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Topics.Should().HaveCountGreaterThan(0);
        result.Topics.Should().Contain(t => t.Topic == "Machine Learning");
        result.Topics.Should().Contain(t => t.Topic == "Natural Language Processing");
    }

    [Fact]
    public async Task AnalyzeAsync_DominantTopicSetToHighestScore()
    {
        var markdown = "# Title\n\nSingle paragraph text.";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        if (result!.Topics.Count > 0)
            result.DominantTopic.Should().NotBeEmpty();
    }

    // --- FirstLine truncation ---

    [Fact]
    public async Task AnalyzeAsync_TruncatesLongFirstLineAt77Chars()
    {
        var longText = new string('A', 120);
        var markdown = $"# Title\n\n{longText}";
        DocumentSignals? result = null;
        _sut.AnalysisCompleted += (_, signals) => result = signals;

        await _sut.AnalyzeAsync(markdown);
        await Task.Delay(700);

        result.Should().NotBeNull();
        result!.Segments.Should().ContainSingle();
        result.Segments[0].FirstLine.Should().HaveLength(80); // 77 + "..."
        result.Segments[0].FirstLine.Should().EndWith("...");
    }

    // --- Debounce behavior ---

    [Fact]
    public async Task AnalyzeAsync_SupersedesEarlierAnalysis()
    {
        DocumentSignals? lastResult = null;
        var callCount = 0;
        _sut.AnalysisCompleted += (_, signals) =>
        {
            lastResult = signals;
            Interlocked.Increment(ref callCount);
        };

        // Fire multiple rapid analyses — only the last should complete
        _ = _sut.AnalyzeAsync("# First\n\nFirst content.");
        _ = _sut.AnalyzeAsync("# Second\n\nSecond content.");
        await _sut.AnalyzeAsync("# Third\n\nThird content.");
        await Task.Delay(800);

        lastResult.Should().NotBeNull();
        // The last analysis should contain "Third"
        lastResult!.Headings.Should().ContainSingle(h => h.Text == "Third");
    }
}
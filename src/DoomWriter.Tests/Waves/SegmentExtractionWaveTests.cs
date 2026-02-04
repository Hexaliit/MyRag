using DoomWriter.Models;
using DoomWriter.Waves;
using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Tests.Waves;

public class SegmentExtractionWaveTests
{
    private readonly SegmentExtractionWave _wave = new();

    [Fact]
    public async Task ExtractsSegmentsFromParagraphs()
    {
        var markdown = "# Title\n\nFirst paragraph.\n\nSecond paragraph.";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var segments = signals.First(s => s.Key == "doc.structure.segments").GetValue<List<AnalyzedSegment>>()!;
        segments.Should().HaveCount(2);
        segments[0].Text.Should().Contain("First");
        segments[1].Text.Should().Contain("Second");
    }

    [Fact]
    public async Task SkipsHeadingsAsSegments()
    {
        var markdown = "# Title\n\n## Section\n\nActual content.";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var segments = signals.First(s => s.Key == "doc.structure.segments").GetValue<List<AnalyzedSegment>>()!;
        segments.Should().ContainSingle();
        segments[0].Text.Should().Be("Actual content.");
    }

    [Fact]
    public async Task SegmentsHavePositionOrder()
    {
        var markdown = "# Title\n\nA.\n\nB.\n\nC.";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var segments = signals.First(s => s.Key == "doc.structure.segments").GetValue<List<AnalyzedSegment>>()!;
        segments[0].Position.Should().Be(0);
        segments[1].Position.Should().Be(1);
        segments[2].Position.Should().Be(2);
    }

    [Fact]
    public async Task SegmentSalienceReflectsVocabularyRichness()
    {
        var markdown =
            "# Title\n\nThe the the the the the the.\n\nDiverse varied rich complex nuanced articulate expressive prose writing content.";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var segments = signals.First(s => s.Key == "doc.structure.segments").GetValue<List<AnalyzedSegment>>()!;
        segments.Should().HaveCount(2);
        segments[1].Salience.Should().BeGreaterThan(segments[0].Salience);
    }

    [Fact]
    public async Task TruncatesLongFirstLine()
    {
        var longText = new string('A', 120);
        var markdown = $"# Title\n\n{longText}";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var segments = signals.First(s => s.Key == "doc.structure.segments").GetValue<List<AnalyzedSegment>>()!;
        segments[0].FirstLine.Should().HaveLength(80); // 77 + "..."
        segments[0].FirstLine.Should().EndWith("...");
    }

    [Fact]
    public async Task CachesSegmentsForDownstreamWaves()
    {
        var ctx = new AnalysisContext();
        await _wave.AnalyzeAsync("# Title\n\nParagraph.", ctx);

        ctx.HasCached("segments").Should().BeTrue();
        ctx.GetCached<List<AnalyzedSegment>>("segments").Should().ContainSingle();
    }

    [Fact]
    public async Task ExtractsInlineEntitiesInSegments()
    {
        var markdown = "# Title\n\nHacker News is great. The `EmbeddingService` is fast.";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var segments = signals.First(s => s.Key == "doc.structure.segments").GetValue<List<AnalyzedSegment>>()!;
        segments[0].EntityNames.Should().Contain("Hacker News");
        segments[0].EntityNames.Should().Contain("EmbeddingService");
    }
}
using DoomWriter.Models;
using DoomWriter.Waves;
using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Tests.Waves;

public class HeadingExtractionWaveTests
{
    private readonly HeadingExtractionWave _wave = new();

    [Fact]
    public async Task ExtractsH1Heading()
    {
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync("# Hello World\n\nBody text.", ctx)).ToList();

        var headings = signals.First(s => s.Key == "doc.structure.headings").GetValue<List<HeadingItem>>()!;
        headings.Should().ContainSingle();
        headings[0].Text.Should().Be("Hello World");
        headings[0].Level.Should().Be(1);
        headings[0].LineNumber.Should().Be(1);
    }

    [Fact]
    public async Task ExtractsMultipleLevelHeadings()
    {
        var markdown = "# Title\n\nParagraph.\n\n## Section 1\n\nText.\n\n### Subsection\n\nMore.";
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync(markdown, ctx)).ToList();

        var headings = signals.First(s => s.Key == "doc.structure.headings").GetValue<List<HeadingItem>>()!;
        headings.Should().HaveCount(3);
        headings[0].Level.Should().Be(1);
        headings[1].Level.Should().Be(2);
        headings[2].Level.Should().Be(3);
    }

    [Fact]
    public async Task EmitsHeadingCountSignal()
    {
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync("# A\n\n## B\n\n### C", ctx)).ToList();

        signals.First(s => s.Key == "doc.structure.heading_count").GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task CachesHeadingsForDownstreamWaves()
    {
        var ctx = new AnalysisContext();
        await _wave.AnalyzeAsync("# Title\n\n## Section", ctx);

        ctx.HasCached("headings").Should().BeTrue();
        ctx.GetCached<List<HeadingItem>>("headings").Should().HaveCount(2);
    }

    [Fact]
    public async Task NoHeadingsInPlainText()
    {
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync("Just plain text.", ctx)).ToList();

        signals.First(s => s.Key == "doc.structure.heading_count").GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void ShouldRun_ReturnsFalseForEmpty()
    {
        var ctx = new AnalysisContext();
        _wave.ShouldRun("  ", ctx).Should().BeFalse();
    }
}
using Mostlylucid.Summarizer.Core.Analysis;
using DoomWriter.Waves;

namespace DoomWriter.Tests.Waves;

public class WordCountWaveTests
{
    private readonly WordCountWave _wave = new();

    [Fact]
    public async Task CountsWordsCorrectly()
    {
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync("# Title\n\nOne two three four five.", ctx)).ToList();

        signals.First(s => s.Key == "doc.metrics.word_count").GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task EmptyContentReturnsZero()
    {
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync("", ctx)).ToList();

        signals.First(s => s.Key == "doc.metrics.word_count").GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task WhitespaceOnlyReturnsZero()
    {
        var ctx = new AnalysisContext();
        var signals = (await _wave.AnalyzeAsync("   \n\t  ", ctx)).ToList();

        signals.First(s => s.Key == "doc.metrics.word_count").GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void AlwaysShouldRun()
    {
        _wave.ShouldRun("", new AnalysisContext()).Should().BeTrue();
    }
}

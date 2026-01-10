using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioSummarizer.Tests.Waves;

public class ContentClassifierWaveTests
{
    private readonly ContentClassifierWave _wave;
    private readonly string _testAudioPath;

    public ContentClassifierWaveTests()
    {
        var logger = new Mock<ILogger<ContentClassifierWave>>().Object;
        _wave = new ContentClassifierWave(logger);
        _testAudioPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.wav");
    }

    [Fact]
    public void Priority_ShouldBe70()
    {
        _wave.Priority.Should().Be(70);
    }

    [Fact]
    public void Name_ShouldBeContentClassifierWave()
    {
        _wave.Name.Should().Be("ContentClassifierWave");
    }

    [Fact]
    public void ShouldRun_WhenEnabled_ReturnsTrue()
    {
        // Arrange
        var context = new AnalysisContext();

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRun_WithAnyAudio_ReturnsTrue()
    {
        // Arrange
        var context = new AnalysisContext();

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert - ContentClassifierWave always runs
        result.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudio_ExtractsContentType()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            return;
        }

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        signals.Should().NotBeEmpty();
        signals.Should().Contain(s => s.Name == "audio.content_type");

        var contentTypeSignal = signals.First(s => s.Name == "audio.content_type");
        contentTypeSignal.Value.Should().BeOneOf("speech", "music", "mixed", "silence");
        contentTypeSignal.Type.Should().Be(SignalType.Classification);
        contentTypeSignal.Confidence.Should().NotBeNull();
        contentTypeSignal.Confidence.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudio_ExtractsAcousticFeatures()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            return;
        }

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert - Check for signals that are actually emitted
        signals.Should().Contain(s => s.Name == "audio.zero_crossing_rate");
        signals.Should().Contain(s => s.Name == "audio.silence_ratio");

        // ZCR and silence ratio should be doubles >= 0
        var zcrSignal = signals.First(s => s.Name == "audio.zero_crossing_rate");
        var silenceSignal = signals.First(s => s.Name == "audio.silence_ratio");

        zcrSignal.Value.Should().BeOfType<double>();
        ((double)zcrSignal.Value).Should().BeGreaterThanOrEqualTo(0);

        silenceSignal.Value.Should().BeOfType<double>();
        ((double)silenceSignal.Value).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task AnalyzeAsync_ExtractsLikelihoodScores()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            return;
        }

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        signals.Should().Contain(s => s.Name == "audio.speech_likelihood");
        signals.Should().Contain(s => s.Name == "audio.music_likelihood");

        var speechLikelihood = (double)signals.First(s => s.Name == "audio.speech_likelihood").Value;
        var musicLikelihood = (double)signals.First(s => s.Name == "audio.music_likelihood").Value;

        speechLikelihood.Should().BeInRange(0.0, 1.0);
        musicLikelihood.Should().BeInRange(0.0, 1.0);
    }
}

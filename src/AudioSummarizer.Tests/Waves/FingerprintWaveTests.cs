using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using AudioSummarizer.Core.Services.Fingerprinting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioSummarizer.Tests.Waves;

public class FingerprintWaveTests
{
    private readonly FingerprintWave _wave;
    private readonly string _testAudioPath;

    public FingerprintWaveTests()
    {
        var fingerprintService = new PureNetFingerprintService(
            new Mock<ILogger<PureNetFingerprintService>>().Object);
        var config = Options.Create(new AudioConfig());
        var logger = new Mock<ILogger<FingerprintWave>>().Object;

        _wave = new FingerprintWave(fingerprintService, config, logger);
        _testAudioPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.wav");
    }

    [Fact]
    public void Priority_ShouldBe90()
    {
        _wave.Priority.Should().Be(90);
    }

    [Fact]
    public void Name_ShouldBeFingerprintWave()
    {
        _wave.Name.Should().Be("FingerprintWave");
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
    public async Task AnalyzeAsync_WithValidAudio_ExtractsFingerprint()
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
        signals.Should().Contain(s => s.Name == "audio.fingerprint.hash");
        signals.Should().Contain(s => s.Name == "audio.fingerprint.type");

        var fingerprintSignal = signals.First(s => s.Name == "audio.fingerprint.hash");
        fingerprintSignal.Value.Should().NotBeNull();
        fingerprintSignal.Type.Should().Be(SignalType.Identity);
    }

    [Fact]
    public async Task AnalyzeAsync_SameAudio_ProducesSameFingerprint()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            return;
        }

        var context = new AnalysisContext();

        // Act - Run twice
        var signals1 = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();
        var signals2 = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        var fingerprint1 = signals1.First(s => s.Name == "audio.fingerprint.hash").Value.ToString();
        var fingerprint2 = signals2.First(s => s.Name == "audio.fingerprint.hash").Value.ToString();

        // Assert - Should be identical
        fingerprint1.Should().Be(fingerprint2);
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudio_FingerprintTypeIsSpectralPeaks()
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
        var typeSignal = signals.First(s => s.Name == "audio.fingerprint.type");
        typeSignal.Value.Should().Be("spectral_peaks");
    }
}

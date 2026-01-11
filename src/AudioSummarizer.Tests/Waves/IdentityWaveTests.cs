using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioSummarizer.Tests.Waves;

public class IdentityWaveTests
{
    private readonly IdentityWave _wave;
    private readonly string _testAudioPath;

    public IdentityWaveTests()
    {
        var logger = new Mock<ILogger<IdentityWave>>().Object;
        _wave = new IdentityWave(logger);

        _testAudioPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.wav");
    }

    [Fact]
    public void Priority_ShouldBe100()
    {
        // Arrange & Act & Assert
        _wave.Priority.Should().Be(100);
    }

    [Fact]
    public void Name_ShouldBeIdentityWave()
    {
        // Arrange & Act & Assert
        _wave.Name.Should().Be("IdentityWave");
    }

    [Fact]
    public void ShouldRun_WithValidAudioFile_ReturnsTrue()
    {
        // Arrange
        var context = new AnalysisContext();

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudioFile_ExtractsAllSignals()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            // Skip test if sample file doesn't exist
            return;
        }

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        signals.Should().NotBeEmpty();

        // Should have hash signals
        signals.Should().Contain(s => s.Name == "audio.hash.sha256");
        signals.Should().Contain(s => s.Name == "audio.hash.pcm_sha256");

        // Should have file metadata
        signals.Should().Contain(s => s.Name == "audio.format");

        // Should have audio properties
        signals.Should().Contain(s => s.Name == "audio.duration_seconds");
        signals.Should().Contain(s => s.Name == "audio.sample_rate");
        signals.Should().Contain(s => s.Name == "audio.channels");
        signals.Should().Contain(s => s.Name == "audio.bits_per_sample");
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudioFile_HashesAreConsistent()
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

        var hash1 = signals1.First(s => s.Name == "audio.hash.sha256").Value.ToString();
        var hash2 = signals2.First(s => s.Name == "audio.hash.sha256").Value.ToString();

        // Assert - Hashes should be identical
        hash1.Should().Be(hash2);
        hash1.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudioFile_DurationIsPositive()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            return;
        }

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();
        var durationSignal = signals.First(s => s.Name == "audio.duration_seconds");

        // Assert
        durationSignal.Value.Should().BeOfType<double>();
        ((double)durationSignal.Value).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudioFile_AllSignalsHaveIdentityType()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
        {
            return;
        }

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert - Hash signals should have Identity type
        var hashSignals = signals.Where(s => s.Name.Contains("hash"));
        hashSignals.Should().AllSatisfy(s => s.Type.Should().Be(SignalType.Identity));
    }
}

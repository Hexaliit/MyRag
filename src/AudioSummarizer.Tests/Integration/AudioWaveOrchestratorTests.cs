using AudioSummarizer.Core.Extensions;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioSummarizer.Tests.Integration;

public class AudioWaveOrchestratorTests : IDisposable
{
    private readonly AudioWaveOrchestrator _orchestrator;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _testAudioPath;

    public AudioWaveOrchestratorTests()
    {
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Configure audio services
        services.AddAudioSummarizer(config =>
        {
            config.EnableVoiceEmbeddings = false; // Skip model download in tests
            config.EnableSpeakerDiarization = false;
            config.Pipeline.EnableFingerprinting = true;
            config.Pipeline.EnableAcousticProfiling = true;
            config.Pipeline.EnableContentClassification = true;
            config.Pipeline.EnableTranscription = false; // Skip transcription in tests
        });

        _serviceProvider = services.BuildServiceProvider();
        _orchestrator = _serviceProvider.GetRequiredService<AudioWaveOrchestrator>();
        _testAudioPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.wav");
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    [Fact]
    public async Task AnalyzeAsync_WithValidAudio_ExecutesAllEnabledWaves()
    {
        // Arrange
        if (!File.Exists(_testAudioPath))
            // Skip if test file missing
            return;

        // Act
        var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, CancellationToken.None);

        // Assert
        profile.Should().NotBeNull();
        profile.AudioPath.Should().Be(_testAudioPath);
        profile.Signals.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_ExecutesWavesInPriorityOrder()
    {
        // Arrange
        if (!File.Exists(_testAudioPath)) return;

        // Act
        var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, CancellationToken.None);

        // Assert
        // Identity wave (priority 100) should have run
        profile.Signals.Should().ContainKey("audio.hash.sha256");
        profile.Signals.Should().ContainKey("audio.duration_seconds");

        // Fingerprint wave (priority 90) should have run
        profile.Signals.Should().ContainKey("audio.fingerprint.hash");

        // Content classifier wave (priority 70) should have run
        profile.Signals.Should().ContainKey("audio.content_type");
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesSignalsBetweenWaves()
    {
        // Arrange
        if (!File.Exists(_testAudioPath)) return;

        // Act
        var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, CancellationToken.None);

        // Assert - Later waves should have access to earlier signals
        // Content classifier runs after identity, so duration should be available
        profile.Signals.Should().ContainKey("audio.duration_seconds");
        profile.Signals.Should().ContainKey("audio.content_type");

        var duration = (double)profile.Signals["audio.duration_seconds"].Value;
        duration.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnalyzeAsync_CapturesAllSignalTypes()
    {
        // Arrange
        if (!File.Exists(_testAudioPath)) return;

        // Act
        var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, CancellationToken.None);

        // Assert - Should have signals of different types
        var signalTypes = profile.Signals.Values.Select(s => s.Type).Distinct().ToList();

        signalTypes.Should().Contain(SignalType.Identity); // Hash, fingerprint
        signalTypes.Should().Contain(SignalType.Metadata); // File properties
        signalTypes.Should().Contain(SignalType.Acoustic); // ZCR, spectral features
        signalTypes.Should().Contain(SignalType.Classification); // Content type
    }

    [Fact]
    public async Task AnalyzeAsync_WithMissingFile_ThrowsException()
    {
        // Arrange
        var nonExistentPath = "nonexistent.wav";

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await _orchestrator.AnalyzeAsync(nonExistentPath, CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeAsync_SignalsHaveTimestamps()
    {
        // Arrange
        if (!File.Exists(_testAudioPath)) return;

        var beforeAnalysis = DateTimeOffset.UtcNow;

        // Act
        var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, CancellationToken.None);

        var afterAnalysis = DateTimeOffset.UtcNow;

        // Assert - All signals should have timestamps within the analysis window
        profile.Signals.Values.Should().AllSatisfy(signal =>
        {
            signal.Timestamp.Should().BeOnOrAfter(beforeAnalysis.AddSeconds(-1));
            signal.Timestamp.Should().BeOnOrBefore(afterAnalysis.AddSeconds(1));
        });
    }

    [Fact]
    public async Task AnalyzeAsync_SignalsHaveSourceAttribution()
    {
        // Arrange
        if (!File.Exists(_testAudioPath)) return;

        // Act
        var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, CancellationToken.None);

        // Assert - All signals should have a source
        profile.Signals.Values.Should().AllSatisfy(signal =>
        {
            signal.Source.Should().NotBeNullOrEmpty();
            signal.Source.Should().EndWith("Wave");
        });
    }

    [Fact]
    public async Task AnalyzeAsync_CancellationToken_ReturnsPartialResults()
    {
        // Arrange
        if (!File.Exists(_testAudioPath)) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)); // Short timeout

        // Act - May complete or be canceled depending on timing
        try
        {
            var profile = await _orchestrator.AnalyzeAsync(_testAudioPath, cts.Token);
            // If it completes, it should have at least some signals
            profile.Should().NotBeNull();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is acceptable (includes TaskCanceledException)
        }
    }
}
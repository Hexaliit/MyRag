using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using AudioSummarizer.Core.Services.Audio;
using AudioSummarizer.Core.Services.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioSummarizer.Tests.Waves;

public class SpeakerDiarizationWaveTests
{
    private readonly Mock<SpeakerDiarizationService> _mockDiarizationService;
    private readonly Mock<AudioSegmentExtractor> _mockSegmentExtractor;
    private readonly string _testAudioPath;
    private readonly SpeakerDiarizationWave _wave;

    public SpeakerDiarizationWaveTests()
    {
        var logger = new Mock<ILogger<SpeakerDiarizationWave>>().Object;
        var config = Options.Create(new AudioConfig
        {
            EnableSpeakerDiarization = true,
            VoiceEmbedding = new VoiceEmbeddingConfig
            {
                MinDurationSeconds = 1.0
            }
        });

        // Use MockBehavior.Loose to allow all virtual methods to be called
        _mockDiarizationService = new Mock<SpeakerDiarizationService>(
            MockBehavior.Loose,
            new Mock<ILogger<SpeakerDiarizationService>>().Object,
            null!, // Not used in these tests (DiarizeAsync is mocked)
            config
        );

        _mockSegmentExtractor = new Mock<AudioSegmentExtractor>(
            MockBehavior.Loose,
            new Mock<ILogger<AudioSegmentExtractor>>().Object
        );

        // Setup default return for ExtractSpeakerSamplesAsync
        _mockSegmentExtractor
            .Setup(s => s.ExtractSpeakerSamplesAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<SpeakerTurn>>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        _wave = new SpeakerDiarizationWave(
            _mockDiarizationService.Object,
            _mockSegmentExtractor.Object,
            config,
            logger);
        _testAudioPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.wav");
    }

    [Fact]
    public void Priority_ShouldBe50()
    {
        _wave.Priority.Should().Be(50);
    }

    [Fact]
    public void Name_ShouldBeSpeakerDiarizationWave()
    {
        _wave.Name.Should().Be("SpeakerDiarizationWave");
    }

    [Fact]
    public void ShouldRun_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        var config = Options.Create(new AudioConfig { EnableSpeakerDiarization = false });
        var wave = new SpeakerDiarizationWave(
            _mockDiarizationService.Object,
            _mockSegmentExtractor.Object,
            config,
            new Mock<ILogger<SpeakerDiarizationWave>>().Object);
        var context = new AnalysisContext();

        // Act
        var result = wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_WhenMusicContent_ReturnsFalse()
    {
        // Arrange
        var context = new AnalysisContext();
        context.AddSignals(new[]
        {
            new Signal
            {
                Name = "audio.content_type",
                Value = "music",
                Type = SignalType.Classification,
                Source = "ContentClassifierWave"
            }
        });

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_WhenSilence_ReturnsFalse()
    {
        // Arrange
        var context = new AnalysisContext();
        context.AddSignals(new[]
        {
            new Signal
            {
                Name = "audio.content_type",
                Value = "silence",
                Type = SignalType.Classification,
                Source = "ContentClassifierWave"
            }
        });

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_WhenSpeech_ReturnsTrue()
    {
        // Arrange
        var context = new AnalysisContext();
        context.AddSignals(new[]
        {
            new Signal
            {
                Name = "audio.content_type",
                Value = "speech",
                Type = SignalType.Classification,
                Source = "ContentClassifierWave"
            },
            new Signal
            {
                Name = "audio.duration_seconds",
                Value = 10.0,
                Type = SignalType.Metadata,
                Source = "IdentityWave"
            }
        });

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRun_WhenTooShort_ReturnsFalse()
    {
        // Arrange
        var context = new AnalysisContext();
        context.AddSignals(new[]
        {
            new Signal
            {
                Name = "audio.duration_seconds",
                Value = 0.5, // Less than 1.0 second minimum
                Type = SignalType.Metadata,
                Source = "IdentityWave"
            }
        });

        // Act
        var result = _wave.ShouldRun(_testAudioPath, context);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_WithTwoSpeakers_EmitsCorrectSignals()
    {
        // Arrange
        var mockResult = new DiarizationResult
        {
            SpeakerCount = 2,
            Turns = new List<SpeakerTurn>
            {
                new() { SpeakerId = "SPEAKER_00", StartSeconds = 0.0, EndSeconds = 5.0, Confidence = 0.9 },
                new() { SpeakerId = "SPEAKER_01", StartSeconds = 5.0, EndSeconds = 10.0, Confidence = 0.85 },
                new() { SpeakerId = "SPEAKER_00", StartSeconds = 10.0, EndSeconds = 15.0, Confidence = 0.92 }
            }
        };

        _mockDiarizationService
            .Setup(s => s.DiarizeAsync(_testAudioPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult);

        var context = new AnalysisContext();
        context.AddSignals(new[]
        {
            new Signal
            {
                Name = "audio.duration_seconds",
                Value = 15.0,
                Type = SignalType.Metadata,
                Source = "IdentityWave"
            }
        });

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        signals.Should().NotBeEmpty();

        // Check speaker count
        var speakerCountSignal = signals.FirstOrDefault(s => s.Name == "speaker.count");
        speakerCountSignal.Should().NotBeNull();
        speakerCountSignal!.Value.Should().Be(2);

        // Check classification
        var classificationSignal = signals.FirstOrDefault(s => s.Name == "speaker.classification");
        classificationSignal.Should().NotBeNull();
        classificationSignal!.Value.Should().Be("two_speakers");

        // Check turn count
        var turnCountSignal = signals.FirstOrDefault(s => s.Name == "speaker.turn_count");
        turnCountSignal.Should().NotBeNull();
        turnCountSignal!.Value.Should().Be(3);

        // Check confidence
        var confidenceSignal = signals.FirstOrDefault(s => s.Name == "speaker.diarization_confidence");
        confidenceSignal.Should().NotBeNull();
        ((double)confidenceSignal!.Value).Should().BeApproximately(0.89, 0.01); // Average of 0.9, 0.85, 0.92

        // Check turns JSON
        var turnsSignal = signals.FirstOrDefault(s => s.Name == "speaker.turns");
        turnsSignal.Should().NotBeNull();
        turnsSignal!.Value.Should().BeOfType<string>();
    }

    [Fact]
    public async Task AnalyzeAsync_WithSingleSpeaker_EmitsCorrectClassification()
    {
        // Arrange
        var mockResult = new DiarizationResult
        {
            SpeakerCount = 1,
            Turns = new List<SpeakerTurn>
            {
                new() { SpeakerId = "SPEAKER_00", StartSeconds = 0.0, EndSeconds = 10.0, Confidence = 0.95 }
            }
        };

        _mockDiarizationService
            .Setup(s => s.DiarizeAsync(_testAudioPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult);

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        var classificationSignal = signals.FirstOrDefault(s => s.Name == "speaker.classification");
        classificationSignal.Should().NotBeNull();
        classificationSignal!.Value.Should().Be("single_speaker");
    }

    [Fact]
    public async Task AnalyzeAsync_WithMultipleSpeakers_EmitsCorrectClassification()
    {
        // Arrange
        var mockResult = new DiarizationResult
        {
            SpeakerCount = 5,
            Turns = new List<SpeakerTurn>()
        };

        _mockDiarizationService
            .Setup(s => s.DiarizeAsync(_testAudioPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult);

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        var classificationSignal = signals.FirstOrDefault(s => s.Name == "speaker.classification");
        classificationSignal.Should().NotBeNull();
        classificationSignal!.Value.Should().Be("multi_speaker");
    }

    [Fact]
    public async Task AnalyzeAsync_WithNoSpeakers_EmitsCorrectClassification()
    {
        // Arrange
        var mockResult = new DiarizationResult
        {
            SpeakerCount = 0,
            Turns = new List<SpeakerTurn>()
        };

        _mockDiarizationService
            .Setup(s => s.DiarizeAsync(_testAudioPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult);

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        var classificationSignal = signals.FirstOrDefault(s => s.Name == "speaker.classification");
        classificationSignal.Should().NotBeNull();
        classificationSignal!.Value.Should().Be("no_speech");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsMethodSignal()
    {
        // Arrange
        var mockResult = new DiarizationResult
        {
            SpeakerCount = 1,
            Turns = new List<SpeakerTurn>()
        };

        _mockDiarizationService
            .Setup(s => s.DiarizeAsync(_testAudioPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult);

        var context = new AnalysisContext();

        // Act
        var signals = (await _wave.AnalyzeAsync(_testAudioPath, context, CancellationToken.None)).ToList();

        // Assert
        var methodSignal = signals.FirstOrDefault(s => s.Name == "speaker.diarization_method");
        methodSignal.Should().NotBeNull();
        methodSignal!.Value.Should().Be("agglomerative_clustering");
    }
}
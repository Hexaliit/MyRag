using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Services.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioSummarizer.Tests.Services;

public class VoiceEmbeddingServiceTests
{
    [Fact]
    public void CalculateSimilarity_WithIdenticalEmbeddings_ReturnsOne()
    {
        // Arrange
        var logger = new Mock<ILogger<VoiceEmbeddingService>>().Object;
        var downloader = new Mock<VoiceEmbeddingModelDownloader>(
            new Mock<ILogger<VoiceEmbeddingModelDownloader>>().Object,
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new AudioConfig())).Object;

        var config = Options.Create(new AudioConfig());
        var service = new VoiceEmbeddingService(logger, downloader, config);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

        // Act
        var similarity = service.CalculateSimilarity(embedding, embedding);

        // Assert
        similarity.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void CalculateSimilarity_WithOrthogonalEmbeddings_ReturnsZero()
    {
        // Arrange
        var logger = new Mock<ILogger<VoiceEmbeddingService>>().Object;
        var downloader = new Mock<VoiceEmbeddingModelDownloader>(
            new Mock<ILogger<VoiceEmbeddingModelDownloader>>().Object,
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new AudioConfig())).Object;

        var config = Options.Create(new AudioConfig());
        var service = new VoiceEmbeddingService(logger, downloader, config);

        var embedding1 = new float[] { 1.0f, 0.0f };
        var embedding2 = new float[] { 0.0f, 1.0f };

        // Act
        var similarity = service.CalculateSimilarity(embedding1, embedding2);

        // Assert
        similarity.Should().BeApproximately(0.0, 0.0001);
    }

    [Fact]
    public void CalculateSimilarity_WithOppositeEmbeddings_ReturnsNegativeOne()
    {
        // Arrange
        var logger = new Mock<ILogger<VoiceEmbeddingService>>().Object;
        var downloader = new Mock<VoiceEmbeddingModelDownloader>(
            new Mock<ILogger<VoiceEmbeddingModelDownloader>>().Object,
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new AudioConfig())).Object;

        var config = Options.Create(new AudioConfig());
        var service = new VoiceEmbeddingService(logger, downloader, config);

        var embedding1 = new float[] { 1.0f, 0.0f, 0.0f };
        var embedding2 = new float[] { -1.0f, 0.0f, 0.0f };

        // Act
        var similarity = service.CalculateSimilarity(embedding1, embedding2);

        // Assert
        similarity.Should().BeApproximately(-1.0, 0.0001);
    }

    [Fact]
    public void CalculateSimilarity_WithDifferentLengths_ThrowsException()
    {
        // Arrange
        var logger = new Mock<ILogger<VoiceEmbeddingService>>().Object;
        var downloader = new Mock<VoiceEmbeddingModelDownloader>(
            new Mock<ILogger<VoiceEmbeddingModelDownloader>>().Object,
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new AudioConfig())).Object;

        var config = Options.Create(new AudioConfig());
        var service = new VoiceEmbeddingService(logger, downloader, config);

        var embedding1 = new float[] { 1.0f, 2.0f };
        var embedding2 = new float[] { 1.0f, 2.0f, 3.0f };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            service.CalculateSimilarity(embedding1, embedding2));
    }

    [Fact]
    public void CalculateSimilarity_WithZeroVectors_ReturnsZero()
    {
        // Arrange
        var logger = new Mock<ILogger<VoiceEmbeddingService>>().Object;
        var downloader = new Mock<VoiceEmbeddingModelDownloader>(
            new Mock<ILogger<VoiceEmbeddingModelDownloader>>().Object,
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new AudioConfig())).Object;

        var config = Options.Create(new AudioConfig());
        var service = new VoiceEmbeddingService(logger, downloader, config);

        var embedding1 = new float[] { 0.0f, 0.0f, 0.0f };
        var embedding2 = new float[] { 1.0f, 2.0f, 3.0f };

        // Act
        var similarity = service.CalculateSimilarity(embedding1, embedding2);

        // Assert
        similarity.Should().Be(0.0);
    }

    [Fact]
    public void CalculateSimilarity_WithSimilarEmbeddings_ReturnsHighValue()
    {
        // Arrange
        var logger = new Mock<ILogger<VoiceEmbeddingService>>().Object;
        var downloader = new Mock<VoiceEmbeddingModelDownloader>(
            new Mock<ILogger<VoiceEmbeddingModelDownloader>>().Object,
            new Mock<IHttpClientFactory>().Object,
            Options.Create(new AudioConfig())).Object;

        var config = Options.Create(new AudioConfig());
        var service = new VoiceEmbeddingService(logger, downloader, config);

        var embedding1 = new float[] { 1.0f, 2.0f, 3.0f };
        var embedding2 = new float[] { 1.1f, 2.1f, 2.9f }; // Very similar

        // Act
        var similarity = service.CalculateSimilarity(embedding1, embedding2);

        // Assert
        similarity.Should().BeGreaterThan(0.99);
    }
}

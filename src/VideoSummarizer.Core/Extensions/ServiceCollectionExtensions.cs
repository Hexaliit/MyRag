using AudioSummarizer.Core.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Summarizer.Core.Pipeline;
using VideoSummarizer.Core.Pipeline;
using VideoSummarizer.Core.Services;
using VideoSummarizer.Core.Waves;

namespace VideoSummarizer.Core.Extensions;

/// <summary>
/// Extension methods for registering VideoSummarizer services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add VideoSummarizer.Core services to the service collection.
    /// Chains to ImageSummarizer and AudioSummarizer for frame and audio analysis.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddVideoSummarizer(this IServiceCollection services)
    {
        // Register FFmpeg analysis service
        services.AddSingleton<FFmpegAnalysisService>();

        // Register optimization services
        services.AddSingleton<KeyframeDeduplicationService>();
        services.AddSingleton<BatchClipEmbeddingService>();

        // Register video waves in priority order
        services.AddTransient<IVideoWave, NormalizeWave>();           // Priority 1000
        services.AddTransient<IVideoWave, ShotDetectionWave>();       // Priority 900
        services.AddTransient<IVideoWave, KeyframeExtractionWave>();  // Priority 800
        services.AddTransient<IVideoWave, ShotThumbnailWave>();       // Priority 790
        services.AddTransient<IVideoWave, TitleCreditsDetectionWave>(); // Priority 785
        services.AddTransient<IVideoWave, EnhancedCreditsOcrWave>();   // Priority 780
        services.AddTransient<IVideoWave, SubtitleExtractionWave>();  // Priority 750
        services.AddTransient<IVideoWave, ChapterExtractionWave>();   // Priority 740
        services.AddTransient<IVideoWave, TranscriptionWave>();       // Priority 500
        services.AddTransient<IVideoWave, SceneClusteringWave>();     // Priority 400
        services.AddTransient<IVideoWave, EvidenceGenerationWave>();  // Priority 300

        // Register wave coordinator
        services.AddTransient<VideoWaveCoordinator>();

        // Register pipeline
        services.AddTransient<VideoPipeline>();
        services.AddTransient<IPipeline>(sp => sp.GetRequiredService<VideoPipeline>());

        return services;
    }

    /// <summary>
    /// Add VideoSummarizer with explicit orchestrator dependencies.
    /// Use this when you need to configure the chained orchestrators explicitly.
    /// </summary>
    public static IServiceCollection AddVideoSummarizer(
        this IServiceCollection services,
        Action<VideoSummarizerOptions> configure)
    {
        var options = new VideoSummarizerOptions();
        configure(options);

        // Register options
        services.AddSingleton(options);

        // Add base services
        services.AddVideoSummarizer();

        return services;
    }
}

/// <summary>
/// Configuration options for VideoSummarizer.
/// </summary>
public class VideoSummarizerOptions
{
    /// <summary>
    /// Whether to enable keyframe extraction and ImageSummarizer analysis.
    /// Default: true
    /// </summary>
    public bool EnableKeyframeAnalysis { get; set; } = true;

    /// <summary>
    /// Whether to enable audio transcription via AudioSummarizer.
    /// Default: true
    /// </summary>
    public bool EnableTranscription { get; set; } = true;

    /// <summary>
    /// Maximum number of keyframes to analyze per video.
    /// Default: 50
    /// </summary>
    public int MaxKeyframes { get; set; } = 50;

    /// <summary>
    /// Minimum scene duration in seconds.
    /// Default: 5.0
    /// </summary>
    public double MinSceneDuration { get; set; } = 5.0;

    /// <summary>
    /// Cosine similarity threshold for scene boundary detection.
    /// Default: 0.7
    /// </summary>
    public double SceneSimilarityThreshold { get; set; } = 0.7;

    /// <summary>
    /// FFmpeg executable path. Null = auto-detect.
    /// </summary>
    public string? FFmpegPath { get; set; }

    /// <summary>
    /// FFprobe executable path. Null = auto-detect.
    /// </summary>
    public string? FFprobePath { get; set; }
}

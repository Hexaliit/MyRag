using AudioSummarizer.Core.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Summarizer.Core.Pipeline;
using VideoSummarizer.Core.Coordination;
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
        // Register YAML manifest loader (reads waves.yaml configuration)
        services.AddSingleton<VideoWaveManifestLoader>(sp =>
        {
            var loader = new VideoWaveManifestLoader();
            loader.LoadFromEmbedded();
            return loader;
        });

        // Register FFmpeg analysis service
        services.AddSingleton<FFmpegAnalysisService>();

        // Register optimization services
        services.AddSingleton<KeyframeDeduplicationService>();
        services.AddSingleton<BatchClipEmbeddingService>();

        // Register video waves - NEW SIGNAL-BASED ARCHITECTURE
        // Phase 1: Normalization (Priority 1000)
        services.AddTransient<IVideoWave, NormalizeWave>();

        // Phase 2: Shot Detection (Priority 900)
        services.AddTransient<IVideoWave, FFmpegShotDetectionWave>(); // GPU-accelerated

        // Phase 3: Keyframe Pipeline - SIGNAL-BASED DECOMPOSITION (Priority 850-790)
        services.AddTransient<IVideoWave, IFrameDetectionWave>();      // Priority 850 - emits: keyframes.iframes_detected
        services.AddTransient<IVideoWave, KeyframeSelectionWave>();    // Priority 840 - requires: shots.detected, iframes
        services.AddTransient<IVideoWave, ThumbnailExtractionWave>();  // Priority 830 - requires: keyframes.selected
        services.AddTransient<IVideoWave, DeduplicationWave>();        // Priority 820 - requires: thumbnails
        services.AddTransient<IVideoWave, FullResExtractionWave>();    // Priority 810 - requires: deduplicated
        services.AddTransient<IVideoWave, ClipEmbeddingWave>();        // Priority 800 - requires: extracted
        services.AddTransient<IVideoWave, ImageAnalysisWave>();        // Priority 790 - requires: extracted

        // Legacy monolithic wave (deprecated, kept for compatibility)
        // services.AddTransient<IVideoWave, KeyframeExtractionWave>();

        // Phase 4: Additional Visual Processing (Priority 790-740)
        services.AddTransient<IVideoWave, ShotThumbnailWave>();
        services.AddTransient<IVideoWave, TitleCreditsDetectionWave>();
        services.AddTransient<IVideoWave, EnhancedCreditsOcrWave>();
        services.AddTransient<IVideoWave, SubtitleExtractionWave>();
        services.AddTransient<IVideoWave, ChapterExtractionWave>();

        // Phase 5: Audio Processing (Priority 500)
        services.AddTransient<IVideoWave, TranscriptionWave>();

        // Phase 6: Semantic Analysis (Priority 400)
        services.AddTransient<IVideoWave, SceneClusteringWave>();

        // Phase 7: Evidence Generation (Priority 300)
        services.AddTransient<IVideoWave, EvidenceGenerationWave>();

        // Register wave coordinators
        services.AddTransient<VideoWaveCoordinator>();           // Legacy priority-based (for backward compat)
        services.AddScoped<SignalAwareWaveCoordinator>();        // Signal-based coordination (default)

        // Register pipeline (uses signal-aware coordinator by default)
        services.AddScoped<VideoPipeline>();
        services.AddScoped<IPipeline>(sp => sp.GetRequiredService<VideoPipeline>());

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

    /// <summary>
    /// Use FFmpeg GPU-accelerated shot detection (NVDEC) instead of OpenCV.
    /// Much faster on NVIDIA GPUs (5-10x speedup).
    /// Default: true (use GPU when available)
    /// </summary>
    public bool UseGpuShotDetection { get; set; } = true;

    /// <summary>
    /// Enable hardware acceleration for video decoding (NVDEC/VAAPI/D3D11VA).
    /// Default: true
    /// </summary>
    public bool UseHardwareAcceleration { get; set; } = true;
}

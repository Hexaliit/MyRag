using AudioSummarizer.Core.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Summarizer.Core.Pipeline;
using VideoSummarizer.Core.Coordination;
using VideoSummarizer.Core.Pipeline;
using VideoSummarizer.Core.Services;
using VideoSummarizer.Core.Services.External;
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

        // Register media library services
        services.AddSingleton<MediaFilenameParser>();
        services.AddSingleton<MediaLibraryScanner>();
        services.AddSingleton<FaceTrackingService>();
        services.AddSingleton<SubtitleProcessingService>();
        services.AddSingleton<MediaMetadataService>();
        services.AddSingleton<UnifiedIdentityService>();
        services.AddSingleton<ArtifactGenerationService>();

        // Register video waves in priority order
        services.AddTransient<IVideoWave, NormalizeWave>();             // Priority 1000
        services.AddTransient<IVideoWave, ExternalMetadataWave>();      // Priority 990 - NEW
        services.AddTransient<IVideoWave, ShotDetectionWave>();         // Priority 900
        services.AddTransient<IVideoWave, KeyframeExtractionWave>();    // Priority 800
        services.AddTransient<IVideoWave, ShotThumbnailWave>();         // Priority 790
        services.AddTransient<IVideoWave, TitleCreditsDetectionWave>(); // Priority 785
        services.AddTransient<IVideoWave, EnhancedCreditsOcrWave>();    // Priority 780
        services.AddTransient<IVideoWave, SubtitleExtractionWave>();    // Priority 750
        services.AddTransient<IVideoWave, ChapterExtractionWave>();     // Priority 740
        services.AddTransient<IVideoWave, FaceTrackingWave>();          // Priority 650 - NEW
        services.AddTransient<IVideoWave, SubtitleDownloadWave>();      // Priority 600 - NEW
        services.AddTransient<IVideoWave, TranscriptionWave>();         // Priority 500
        services.AddTransient<IVideoWave, SceneClusteringWave>();       // Priority 400
        services.AddTransient<IVideoWave, EvidenceGenerationWave>();    // Priority 300

        // Register wave coordinators
        services.AddTransient<VideoWaveCoordinator>();           // Legacy priority-based (for backward compat)
        services.AddScoped<SignalAwareWaveCoordinator>();        // Signal-based coordination (default)

        // Register pipeline (uses signal-aware coordinator by default)
        services.AddScoped<VideoPipeline>();
        services.AddScoped<IPipeline>(sp => sp.GetRequiredService<VideoPipeline>());

        return services;
    }

    /// <summary>
    /// Add external API clients for metadata enrichment (TMDB, OMDB, OpenSubtitles).
    /// Call this after AddVideoSummarizer() to enable external metadata features.
    /// </summary>
    public static IServiceCollection AddVideoExternalServices(
        this IServiceCollection services,
        Action<ExternalServicesOptions>? configure = null)
    {
        var options = new ExternalServicesOptions();
        configure?.Invoke(options);

        // Configure TMDB
        services.Configure<TmdbOptions>(opt =>
        {
            opt.ApiKey = options.TmdbApiKey;
            opt.ReadAccessToken = options.TmdbReadAccessToken;
        });
        services.AddHttpClient<TmdbClient>();

        // Configure OMDB
        services.Configure<OmdbOptions>(opt =>
        {
            opt.ApiKey = options.OmdbApiKey;
        });
        services.AddHttpClient<OmdbClient>();

        // Configure OpenSubtitles
        services.Configure<OpenSubtitlesOptions>(opt =>
        {
            opt.ApiKey = options.OpenSubtitlesApiKey;
            opt.Username = options.OpenSubtitlesUsername;
            opt.Password = options.OpenSubtitlesPassword;
            opt.UserAgent = options.OpenSubtitlesUserAgent;
        });
        services.AddHttpClient<OpenSubtitlesClient>();

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
    /// Whether to enable external metadata lookup (TMDB/OMDB).
    /// Default: true
    /// </summary>
    public bool EnableExternalMetadata { get; set; } = true;

    /// <summary>
    /// Whether to enable automatic subtitle download from OpenSubtitles.
    /// Default: true
    /// </summary>
    public bool EnableSubtitleDownload { get; set; } = true;

    /// <summary>
    /// Whether to enable face tracking across videos.
    /// Default: true
    /// </summary>
    public bool EnableFaceTracking { get; set; } = true;

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
    /// Cosine similarity threshold for face matching.
    /// Default: 0.75
    /// </summary>
    public double FaceSimilarityThreshold { get; set; } = 0.75;

    /// <summary>
    /// FFmpeg executable path. Null = auto-detect.
    /// </summary>
    public string? FFmpegPath { get; set; }

    /// <summary>
    /// FFprobe executable path. Null = auto-detect.
    /// </summary>
    public string? FFprobePath { get; set; }

    /// <summary>
    /// Preferred languages for subtitle download (ISO 639-1 codes).
    /// Default: ["en"]
    /// </summary>
    public List<string> SubtitleLanguages { get; set; } = ["en"];
}

/// <summary>
/// Configuration options for external API services.
/// </summary>
public class ExternalServicesOptions
{
    /// <summary>
    /// TMDB API key (v3). Get one at https://www.themoviedb.org/settings/api
    /// </summary>
    public string? TmdbApiKey { get; set; }

    /// <summary>
    /// TMDB API Read Access Token (v4). Optional, provides higher rate limits.
    /// </summary>
    public string? TmdbReadAccessToken { get; set; }

    /// <summary>
    /// OMDB API key. Get one at https://www.omdbapi.com/apikey.aspx
    /// </summary>
    public string? OmdbApiKey { get; set; }

    /// <summary>
    /// OpenSubtitles API key. Get one at https://www.opensubtitles.com/consumers
    /// </summary>
    public string? OpenSubtitlesApiKey { get; set; }

    /// <summary>
    /// OpenSubtitles username (for download access).
    /// </summary>
    public string? OpenSubtitlesUsername { get; set; }

    /// <summary>
    /// OpenSubtitles password (for download access).
    /// </summary>
    public string? OpenSubtitlesPassword { get; set; }

    /// <summary>
    /// User agent for OpenSubtitles API requests.
    /// </summary>
    public string? OpenSubtitlesUserAgent { get; set; } = "LucidRAG v1.0";
}

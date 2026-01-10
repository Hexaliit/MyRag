using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;

namespace AudioSummarizer.Core.Extensions;

/// <summary>
/// Service collection extensions for AudioSummarizer
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register AudioSummarizer services
    /// </summary>
    public static IServiceCollection AddAudioSummarizer(
        this IServiceCollection services,
        Action<AudioConfig>? configure = null)
    {
        // Configuration
        if (configure != null)
        {
            services.Configure(configure);
        }

        // Core services
        services.AddSingleton<AudioWaveOrchestrator>();

        // Register waves (Phase 1: Core Infrastructure & Deterministic Substrate)
        services.AddSingleton<IAudioWave, IdentityWave>();

        // TODO: Phase 2-5 waves will be added here
        // services.AddSingleton<IAudioWave, FingerprintWave>();
        // services.AddSingleton<IAudioWave, AcousticProfileWave>();
        // services.AddSingleton<IAudioWave, ContentClassifierWave>();
        // services.AddSingleton<IAudioWave, TranscriptionWave>();
        // services.AddSingleton<IAudioWave, VoiceEmbeddingWave>();
        // services.AddSingleton<IAudioWave, SpeakerDiarizationWave>();

        return services;
    }

    /// <summary>
    /// Register AudioSummarizer services with IConfiguration
    /// </summary>
    public static IServiceCollection AddAudioSummarizer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AudioConfig>(configuration.GetSection(AudioConfig.SectionName));
        return services.AddAudioSummarizer();
    }
}

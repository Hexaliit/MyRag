using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using AudioSummarizer.Core.Services.Fingerprinting;
using AudioSummarizer.Core.Services.Transcription;

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

        // HttpClient for Ollama
        services.AddHttpClient("Ollama");

        // Fingerprinting service (Phase 2)
        services.AddSingleton<IFingerprintService, PureNetFingerprintService>();

        // Transcription services (Phase 3)
        services.AddSingleton<WhisperModelDownloader>();
        services.AddSingleton<ITranscriptionService, WhisperTranscriptionService>();
        services.AddSingleton<ITranscriptionService, OllamaTranscriptionService>();

        // Register waves
        services.AddSingleton<IAudioWave, IdentityWave>();           // Phase 1: Priority 100
        services.AddSingleton<IAudioWave, FingerprintWave>();        // Phase 2: Priority 90
        services.AddSingleton<IAudioWave, TranscriptionWave>();      // Phase 3: Priority 60

        // TODO: Phase 1 (remaining) & Phase 4-5 waves will be added here
        // services.AddSingleton<IAudioWave, AcousticProfileWave>();  // Phase 1: Priority 80
        // services.AddSingleton<IAudioWave, ContentClassifierWave>(); // Phase 1: Priority 70
        // services.AddSingleton<IAudioWave, VoiceEmbeddingWave>();    // Phase 4: Priority 30
        // services.AddSingleton<IAudioWave, SpeakerDiarizationWave>(); // Phase 5: Priority 50

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

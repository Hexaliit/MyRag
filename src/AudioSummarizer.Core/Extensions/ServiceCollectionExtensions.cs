using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using AudioSummarizer.Core.Services.Audio;
using AudioSummarizer.Core.Services.Fingerprinting;
using AudioSummarizer.Core.Services.Transcription;
using AudioSummarizer.Core.Services.Voice;

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

        // HttpClient for HuggingFace model downloads
        services.AddHttpClient("HuggingFace", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10); // Large model downloads
        });

        // Fingerprinting service (Phase 2)
        services.AddSingleton<IFingerprintService, PureNetFingerprintService>();

        // Transcription services (Phase 3)
        services.AddSingleton<WhisperModelDownloader>();
        services.AddSingleton<ITranscriptionService, WhisperTranscriptionService>();
        services.AddSingleton<ITranscriptionService, OllamaTranscriptionService>();

        // Voice embedding service (Phase 4)
        services.AddSingleton<VoiceEmbeddingModelDownloader>();
        services.AddSingleton<VoiceEmbeddingService>();

        // Speaker diarization service (Phase 5)
        services.AddSingleton<SpeakerDiarizationService>();

        // Audio segment extraction
        services.AddSingleton<AudioSegmentExtractor>();

        // Register waves (in priority order)
        services.AddSingleton<IAudioWave, IdentityWave>();           // Phase 1: Priority 100
        services.AddSingleton<IAudioWave, FingerprintWave>();        // Phase 2: Priority 90
        services.AddSingleton<IAudioWave, ContentClassifierWave>();  // Phase 3.5: Priority 70
        services.AddSingleton<IAudioWave, MusicAnalysisWave>();      // Phase 3.6: Priority 65
        services.AddSingleton<IAudioWave, TranscriptionWave>();      // Phase 3: Priority 60
        services.AddSingleton<IAudioWave, SpeakerDiarizationWave>(); // Phase 5: Priority 50
        services.AddSingleton<IAudioWave, VoiceEmbeddingWave>();     // Phase 4: Priority 30

        // TODO: Future waves
        // services.AddSingleton<IAudioWave, AcousticProfileWave>();  // Phase 1: Priority 80

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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services.Embeddings;
using Mostlylucid.DocSummarizer.Services.LmStudio;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Mostlylucid.DocSummarizer.Services.Providers;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Extension methods for registering providers in DI
/// </summary>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    ///     Register all LLM and embedding providers based on configuration
    /// </summary>
    public static IServiceCollection AddDocSummarizerProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register configuration
        services.Configure<UnifiedEmbeddingConfig>(configuration.GetSection("Embedding"));
        services.Configure<LlmProviderConfig>(configuration.GetSection("LlmProvider"));
        services.Configure<LmStudioConfig>(configuration.GetSection("LmStudio"));
        services.Configure<OnnxConfig>(configuration.GetSection("DocSummarizer:Onnx"));

        // Register OnnxConfig as singleton for DI (resolves from IOptions<OnnxConfig>)
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OnnxConfig>>().Value);

        // Register HttpClient factories
        services.AddHttpClient<LmStudioHttpClient>();

        // Register LM Studio client
        services.AddScoped<ILMStudioClient, LmStudioHttpClient>();

        // Register ONNX embedding service
        services.AddSingleton<OnnxEmbeddingService>();

        // Register LLM clients
        services.AddScoped<LMStudioLlmClient>();

        // Register embedding providers
        services.AddScoped<LmStudioEmbeddingProvider>();
        services.AddScoped<OnnxEmbeddingProvider>();

        // Register provider factory
        services.AddSingleton<IProviderFactory, ProviderFactory>();

        // Register interface aliases for the active provider (based on config)
        services.AddScoped<ILlmClient>(sp =>
        {
            var factory = sp.GetRequiredService<IProviderFactory>();
            return factory.GetLlmClient();
        });

        services.AddScoped<IEmbeddingClient>(sp =>
        {
            var factory = sp.GetRequiredService<IProviderFactory>();
            return factory.GetEmbeddingClient();
        });

        // Register LM Studio specific client
        services.AddScoped<ILMStudioClient>(sp =>
        {
            var factory = sp.GetRequiredService<IProviderFactory>();
            return factory.GetLmStudioClient() ?? sp.GetRequiredService<ILMStudioClient>();
        });

        return services;
    }

    /// <summary>
    ///     Add ONNX embedding service if not already registered
    /// </summary>
    public static IServiceCollection AddOnnxEmbedding(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OnnxConfig>(configuration.GetSection("DocSummarizer:Onnx"));
        services.AddSingleton<OnnxEmbeddingService>();
        return services;
    }

    /// <summary>
    ///     Add Ollama service if not already registered
    /// </summary>
    public static IServiceCollection AddOllamaService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<Mostlylucid.DocSummarizer.Config.OllamaConfig>(configuration.GetSection("Ollama"));
        services.AddSingleton<OllamaService>();
        return services;
    }
}
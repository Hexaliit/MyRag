using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.LLamaSharp.Services;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.LLamaSharp.Extensions;

/// <summary>
///     Extension methods for registering LLamaSharp local LLM services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds LLamaSharp as the local LLM backend with configuration action.
    /// </summary>
    public static IServiceCollection AddDocSummarizerLLamaSharp(
        this IServiceCollection services,
        Action<LLamaSharpConfig>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<LLamaSharpModelDownloader>(sp =>
        {
            var config = sp.GetService<Microsoft.Extensions.Options.IOptions<LLamaSharpConfig>>()?.Value
                         ?? new LLamaSharpConfig();
            return new LLamaSharpModelDownloader(config);
        });

        services.AddSingleton<LLamaSharpLlmService>(sp =>
        {
            var config = sp.GetService<Microsoft.Extensions.Options.IOptions<LLamaSharpConfig>>()?.Value
                         ?? new LLamaSharpConfig();
            var downloader = sp.GetRequiredService<LLamaSharpModelDownloader>();
            return new LLamaSharpLlmService(config, downloader);
        });

        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<LLamaSharpLlmService>());

        return services;
    }

    /// <summary>
    ///     Adds LLamaSharp with a pre-built config instance (for non-DI usage like DoomSummarizer CLI).
    /// </summary>
    public static IServiceCollection AddDocSummarizerLLamaSharp(
        this IServiceCollection services,
        LLamaSharpConfig config)
    {
        var downloader = new LLamaSharpModelDownloader(config);
        var service = new LLamaSharpLlmService(config, downloader);

        services.AddSingleton(downloader);
        services.AddSingleton(service);
        services.AddSingleton<ILlmService>(service);

        return services;
    }
}

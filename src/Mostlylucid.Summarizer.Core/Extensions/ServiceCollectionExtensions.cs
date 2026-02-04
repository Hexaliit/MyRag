using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.Summarizer.Core.FileAnalysis;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.Summarizer.Core.Extensions;

/// <summary>
///     Extension methods for registering Summarizer.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add the FileSummarizer service for universal file metadata extraction.
    ///     This is automatically called by AddPipelineRegistry.
    /// </summary>
    public static IServiceCollection AddFileSummarizer(this IServiceCollection services)
    {
        services.TryAddSingleton<IFileSummarizer, FileSummarizer>();
        return services;
    }

    /// <summary>
    ///     Add the pipeline registry. Call this after registering all IPipeline implementations.
    ///     Must be scoped because some pipelines (Video, Data) are registered as scoped.
    ///     Also registers the FileSummarizer service.
    /// </summary>
    public static IServiceCollection AddPipelineRegistry(this IServiceCollection services)
    {
        // Add FileSummarizer as a foundation service
        services.AddFileSummarizer();

        services.AddScoped<IPipelineRegistry>(sp =>
        {
            var pipelines = sp.GetServices<IPipeline>();
            return new PipelineRegistry(pipelines);
        });

        return services;
    }

    /// <summary>
    ///     Register a pipeline implementation.
    /// </summary>
    public static IServiceCollection AddPipeline<TPipeline>(this IServiceCollection services)
        where TPipeline : class, IPipeline
    {
        services.AddSingleton<IPipeline, TPipeline>();
        services.AddSingleton<TPipeline>();
        return services;
    }
}
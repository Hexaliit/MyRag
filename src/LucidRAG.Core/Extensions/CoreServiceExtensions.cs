using LucidRAG.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LucidRAG.Core.Extensions;

/// <summary>
/// Extension methods for registering core LucidRAG services.
/// </summary>
public static class CoreServiceExtensions
{
    /// <summary>
    /// Adds core LucidRAG services including date extraction, feature services, etc.
    /// </summary>
    public static IServiceCollection AddLucidRagCoreServices(this IServiceCollection services)
    {
        // Date extraction - singleton as it's stateless
        services.AddSingleton<IDateExtractionService, DateExtractionService>();

        // Hybrid date extraction (NER + regex) - singleton as it wraps stateless services
        services.AddSingleton<IHybridDateExtractionService>(sp =>
            new HybridDateExtractionService(sp.GetRequiredService<IDateExtractionService>()));

        // Feature embeddings - scoped for DbContext access
        services.AddScoped<IFeatureEmbeddingService, FeatureEmbeddingService>();

        // Segment graph - scoped for DbContext access
        services.AddScoped<ISegmentGraphService, SegmentGraphService>();

        return services;
    }
}

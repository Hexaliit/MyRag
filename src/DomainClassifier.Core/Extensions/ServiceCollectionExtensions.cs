using DomainClassifier.Core.Configuration;
using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Services;
using DomainClassifier.Core.Services.Onnx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DomainClassifier.Core.Extensions;

/// <summary>
///     Extension methods for registering DomainClassifier.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add the core domain classifier infrastructure (ONNX services, model downloader).
    ///     Called automatically by domain-specific AddDomain*() methods.
    /// </summary>
    public static IServiceCollection AddDomainClassifierCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IOnnxClassifierService, OnnxClassifierService>();
        services.TryAddSingleton<IOnnxNerService, OnnxNerService>();
        services.TryAddSingleton<ClassifierModelDownloader>();
        return services;
    }

    /// <summary>
    ///     Add the domain plugin registry. Call this AFTER registering all domain plugins.
    ///     Discovers all IDomainPlugin registrations from DI.
    /// </summary>
    public static IServiceCollection AddDomainClassifierRegistry(this IServiceCollection services)
    {
        services.AddDomainClassifierCore();

        // Configure default options if not already configured
        services.TryAddSingleton(Microsoft.Extensions.Options.Options.Create(new DomainClassifierConfig()));

        services.AddScoped<IDomainPluginRegistry, DomainPluginRegistry>();
        services.AddScoped<DomainEnrichmentService>();

        return services;
    }
}

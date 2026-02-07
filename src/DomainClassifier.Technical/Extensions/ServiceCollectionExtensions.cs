using DomainClassifier.Core.Extensions;
using DomainClassifier.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DomainClassifier.Technical.Extensions;

/// <summary>
///     Extension methods for registering the Technical/Academic domain plugin.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add the Technical/Academic domain plugin for content enrichment.
    ///     Automatically registers DomainClassifier.Core if not already registered.
    /// </summary>
    public static IServiceCollection AddDomainTechnical(this IServiceCollection services)
    {
        services.AddDomainClassifierCore();
        services.AddSingleton<IDomainPlugin, TechnicalDomainPlugin>();
        return services;
    }
}

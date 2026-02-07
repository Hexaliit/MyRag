using DomainClassifier.Core.Extensions;
using DomainClassifier.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DomainClassifier.Narrative.Extensions;

/// <summary>
///     Extension methods for registering the Narrative domain plugin.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add the Narrative domain plugin for content enrichment.
    ///     Automatically registers DomainClassifier.Core if not already registered.
    /// </summary>
    public static IServiceCollection AddDomainNarrative(this IServiceCollection services)
    {
        services.AddDomainClassifierCore();
        services.AddSingleton<IDomainPlugin, NarrativeDomainPlugin>();
        return services;
    }
}

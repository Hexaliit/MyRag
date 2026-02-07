using DomainClassifier.Core.Extensions;
using DomainClassifier.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DomainClassifier.Financial.Extensions;

/// <summary>
///     Extension methods for registering the Financial domain plugin.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add the Financial domain plugin for content enrichment.
    ///     Automatically registers DomainClassifier.Core if not already registered.
    /// </summary>
    public static IServiceCollection AddDomainFinancial(this IServiceCollection services)
    {
        services.AddDomainClassifierCore();
        services.AddSingleton<IDomainPlugin, FinancialDomainPlugin>();
        return services;
    }
}

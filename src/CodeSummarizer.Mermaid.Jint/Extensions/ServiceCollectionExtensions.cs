using CodeSummarizer.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSummarizer.Mermaid.Jint.Extensions;

/// <summary>
///     DI registration for the Jint-based mermaid parser.
///     Call <see cref="AddMermaidJintParser" /> after <c>AddCodeSummarizer()</c>
///     to override the default regex parser with the Jint-based one.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Register the Jint-based mermaid parser, overriding the default regex parser.
    ///     Last-registration-wins: this replaces <see cref="RegexMermaidParser" /> with
    ///     <see cref="JintMermaidParser" /> which falls back to regex on any failure.
    /// </summary>
    /// <remarks>
    ///     Usage:
    ///     <code>
    ///     services.AddCodeSummarizer();          // Registers RegexMermaidParser
    ///     services.AddMermaidJintParser();        // Overrides with JintMermaidParser
    ///     </code>
    /// </remarks>
    public static IServiceCollection AddMermaidJintParser(this IServiceCollection services)
    {
        // Remove any existing IMermaidParser registration so we can replace it
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IMermaidParser));
        if (existing != null)
            services.Remove(existing);

        services.AddSingleton<IMermaidParser, JintMermaidParser>();
        return services;
    }
}
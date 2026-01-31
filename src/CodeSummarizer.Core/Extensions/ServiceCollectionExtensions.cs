using CodeSummarizer.Parsing;
using CodeSummarizer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeSummarizer.Extensions;

/// <summary>
///     DI registration for CodeSummarizer services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Register CodeSummarizer services. Call after registering OllamaService
    ///     to enable LLM-based descriptions; otherwise structural-only.
    /// </summary>
    public static IServiceCollection AddCodeSummarizer(this IServiceCollection services)
    {
        services.TryAddSingleton<IMermaidParser, RegexMermaidParser>();
        services.AddSingleton<ICodeSummarizer, CodeSummarizerService>();
        return services;
    }
}

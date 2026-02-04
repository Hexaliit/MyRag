using DoomSummarizer.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.Summarizers.Reader.Markdown;

/// <summary>
///     DI registration for the Markdown reader.
/// </summary>
public static class MarkdownReaderExtensions
{
    /// <summary>
    ///     Register the Markdown/text document reader.
    /// </summary>
    public static IServiceCollection AddMarkdownReader(this IServiceCollection services)
    {
        services.AddSingleton<MarkdownReader>();
        services.AddSingleton<IDocumentReader>(sp => sp.GetRequiredService<MarkdownReader>());
        return services;
    }
}
using DoomSummarizer.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.Summarizers.Reader.Gutenberg;

/// <summary>
/// DI registration for the Gutenberg reader.
/// </summary>
public static class GutenbergReaderExtensions
{
    /// <summary>
    /// Register the Project Gutenberg document reader.
    /// </summary>
    public static IServiceCollection AddGutenbergReader(this IServiceCollection services)
    {
        services.AddSingleton<GutenbergReader>();
        services.AddSingleton<IDocumentReader>(sp => sp.GetRequiredService<GutenbergReader>());
        return services;
    }
}

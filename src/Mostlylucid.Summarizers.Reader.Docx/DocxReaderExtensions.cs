using DoomSummarizer.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.Summarizers.Reader.Docx;

/// <summary>
/// DI registration for the DOCX reader.
/// </summary>
public static class DocxReaderExtensions
{
    /// <summary>
    /// Register the DOCX document reader.
    /// </summary>
    public static IServiceCollection AddDocxReader(this IServiceCollection services)
    {
        services.AddSingleton<DocxReader>();
        services.AddSingleton<IDocumentReader>(sp => sp.GetRequiredService<DocxReader>());
        return services;
    }
}

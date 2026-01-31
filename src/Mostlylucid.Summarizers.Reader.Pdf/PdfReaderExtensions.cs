using DoomSummarizer.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.Summarizers.Reader.Pdf;

/// <summary>
/// DI registration for the PDF reader.
/// </summary>
public static class PdfReaderExtensions
{
    /// <summary>
    /// Register the PDF document reader.
    /// </summary>
    public static IServiceCollection AddPdfReader(this IServiceCollection services)
    {
        services.AddSingleton<PdfReader>();
        services.AddSingleton<IDocumentReader>(sp => sp.GetRequiredService<PdfReader>());
        return services;
    }
}
